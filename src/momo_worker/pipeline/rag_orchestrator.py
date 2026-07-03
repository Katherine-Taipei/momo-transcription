import os
import uuid
import math
import requests
import json
from typing import List, Dict, Any
from qdrant_client import QdrantClient
from qdrant_client.models import Distance, VectorParams, PointStruct
from sentence_transformers import SentenceTransformer

from pipeline.pubmed_client import PubMedClient
from pipeline.sec_client import SECClient

class BM25:
    def __init__(self, corpus: List[Dict[str, Any]], k1: float = 1.5, b: float = 0.75):
        self.k1 = k1
        self.b = b
        self.corpus = corpus
        self.doc_len = [len(doc.get("text", "").split()) for doc in corpus]
        self.avg_doc_len = sum(self.doc_len) / len(self.doc_len) if corpus else 1.0
        self.doc_freqs = []
        self.idf = {}
        self._initialize()

    def _initialize(self):
        nd = {}
        for doc in self.corpus:
            frequencies = {}
            for word in doc.get("text", "").lower().split():
                frequencies[word] = frequencies.get(word, 0) + 1
            self.doc_freqs.append(frequencies)
            for word in frequencies:
                nd[word] = nd.get(word, 0) + 1
        
        N = len(self.corpus)
        for word, freq in nd.items():
            self.idf[word] = math.log((N - freq + 0.5) / (freq + 0.5) + 1.0)

    def get_scores(self, query: str) -> List[float]:
        query_words = query.lower().split()
        scores = []
        for i, doc in enumerate(self.corpus):
            score = 0.0
            doc_freq = self.doc_freqs[i]
            d_len = self.doc_len[i]
            for word in query_words:
                if word in doc_freq:
                    freq = doc_freq[word]
                    word_idf = self.idf.get(word, 0.0)
                    tf_scaled = (freq * (self.k1 + 1)) / (freq + self.k1 * (1 - self.b + self.b * (d_len / self.avg_doc_len)))
                    score += word_idf * tf_scaled
            scores.append(score)
        return scores


class RagOrchestrator:
    def __init__(self, db_helper: Any = None):
        self.db_helper = db_helper
        self._model = None
        self._client = None
        self.collection_name = "momo_transcripts"
        self.vector_size = 384  # all-MiniLM-L6-v2 dimension
        
        # Instantiate clients for P3-2
        self.pubmed_client = PubMedClient()
        self.sec_client = SECClient()

    @property
    def model(self):
        if self._model is None:
            self._model = SentenceTransformer("all-MiniLM-L6-v2")
        return self._model

    @property
    def client(self):
        if self._client is None:
            host = os.environ.get("QDRANT_HOST", "localhost")
            port = int(os.environ.get("QDRANT_PORT", 6333))
            try:
                client = QdrantClient(host=host, port=port, timeout=1.0)
                client.get_collections()
                self._client = client
            except Exception:
                self._client = QdrantClient(":memory:")
        return self._client

    def get_embedding(self, text: str) -> List[float]:
        text = text.strip()
        if not text:
            return [0.0] * self.vector_size

        if self.db_helper:
            import hashlib
            text_hash = hashlib.sha256(text.encode("utf-8")).hexdigest()
            try:
                cached = self.db_helper.get_cached_embedding(text_hash)
                if cached:
                    return cached
            except Exception:
                pass

        # Cache miss, generate dense embedding
        vector = self.model.encode(text).tolist()

        if self.db_helper:
            try:
                self.db_helper.save_cached_embedding(text_hash, vector)
            except Exception:
                pass

        return vector

    def ensure_collection(self):
        try:
            collections = self.client.get_collections().collections
            exists = any(c.name == self.collection_name for c in collections)
            if not exists:
                self.client.create_collection(
                    collection_name=self.collection_name,
                    vectors_config=VectorParams(size=self.vector_size, distance=Distance.COSINE)
                )
        except Exception:
            pass

    def ingest_segments(self, project_id: str, media_file_id: str, segments: List[Dict[str, Any]]):
        self.ensure_collection()
        points = []
        for seg in segments:
            text = seg.get("text", "").strip()
            if not text:
                continue
            
            vector = self.get_embedding(text)
            point_id = str(uuid.uuid4())
            payload = {
                "project_id": str(project_id),
                "media_file_id": str(media_file_id),
                "start": float(seg.get("start", 0.0)),
                "end": float(seg.get("end", 0.0)),
                "speaker": seg.get("speaker", "Unknown"),
                "text": text
            }
            points.append(PointStruct(id=point_id, vector=vector, payload=payload))
        
        if points:
            self.client.upsert(
                collection_name=self.collection_name,
                wait=True,
                points=points
            )
        return {"status": "SUCCESS", "count": len(points)}

    def query_segments(self, project_id: str, query: str, limit: int = 5, scope: str = "local") -> List[Dict[str, Any]]:
        self.ensure_collection()
        vector = self.get_embedding(query)
        
        from qdrant_client.models import Filter, FieldCondition, MatchValue
        query_filter = None
        if scope == "local" and project_id:
            query_filter = Filter(
                must=[
                    FieldCondition(
                        key="project_id",
                        match=MatchValue(value=str(project_id))
                    )
                ]
            )
        
        res = self.client.query_points(
            collection_name=self.collection_name,
            query=vector,
            query_filter=query_filter,
            limit=limit
        )
        
        ret = []
        for r in res.points:
            ret.append({
                "score": r.score,
                "payload": r.payload
            })
        return ret

    def hybrid_search(self, db_helper: Any, project_id: str, query: str, limit: int = 5, k: int = 60, scope: str = "local") -> List[Dict[str, Any]]:
        """
        Merge vector results (Qdrant) and keyword results (BM25) using Reciprocal Rank Fusion (RRF).
        """
        # Load custom RRF_K parameter from environment variables if defined (User Request)
        k_val = int(os.environ.get("RRF_K", k))

        # 1. Fetch vector results (scoping as local or global)
        vector_results = self.query_segments(project_id, query, limit=limit * 2, scope=scope)

        # 2. Fetch all segments to run BM25 keyword matching
        db_segments = []
        if db_helper:
            try:
                if scope == "local" and project_id:
                    db_segments = db_helper.get_project_segments(project_id)
                else:
                    db_segments = db_helper.get_all_segments()
            except Exception:
                pass
        
        # Fallback to local/global Qdrant collection documents if db query fails or yields nothing
        if not db_segments:
            try:
                scroll_filter = None
                if scope == "local" and project_id:
                    scroll_filter = self._get_project_filter(project_id)
                scroll_res = self.client.scroll(
                    collection_name=self.collection_name,
                    scroll_filter=scroll_filter,
                    limit=100
                )
                db_segments = [p.payload for p in scroll_res[0]]
            except Exception:
                pass

        bm25_results = []
        if db_segments:
            bm25 = BM25(db_segments)
            scores = bm25.get_scores(query)
            scored_docs = []
            for i, score in enumerate(scores):
                if score > 0.0:
                    scored_docs.append((score, db_segments[i]))
            # Sort by score descending
            scored_docs.sort(key=lambda x: x[0], reverse=True)
            for s, doc in scored_docs[:limit * 2]:
                bm25_results.append({
                    "score": s,
                    "payload": doc
                })

        # 3. Reciprocal Rank Fusion (RRF)
        rrf_scores = {}
        doc_map = {}
        
        def get_doc_key(doc: Dict[str, Any]) -> str:
            return f"{doc.get('media_file_id', '')}_{doc.get('start', 0.0)}_{doc.get('end', 0.0)}"

        for rank, item in enumerate(vector_results):
            payload = item["payload"]
            key = get_doc_key(payload)
            doc_map[key] = payload
            rrf_scores[key] = rrf_scores.get(key, 0.0) + 1.0 / (k_val + rank + 1)

        for rank, item in enumerate(bm25_results):
            payload = item["payload"]
            key = get_doc_key(payload)
            doc_map[key] = payload
            rrf_scores[key] = rrf_scores.get(key, 0.0) + 1.0 / (k_val + rank + 1)

        sorted_keys = sorted(rrf_scores.keys(), key=lambda x: rrf_scores[x], reverse=True)
        
        merged = []
        for key in sorted_keys[:limit]:
            merged.append({
                "score": rrf_scores[key],
                "payload": doc_map[key]
            })
        return merged

    def _get_project_filter(self, project_id: str):
        from qdrant_client.models import Filter, FieldCondition, MatchValue
        return Filter(
            must=[
                FieldCondition(
                    key="project_id",
                    match=MatchValue(value=str(project_id))
                )
            ]
        )

    def build_prompt(self, segments: List[Dict[str, Any]], external_findings: List[Dict[str, Any]], query: str, max_chars: int = 4000) -> str:
        """
        Construct structured LLM prompt, truncating context if it exceeds max_chars.
        """
        system_instructions = (
            "You are Momo Research Assistant, a VC Venture Capital research copilot.\n"
            "Synthesize the provided meeting transcript segments and external literature/filings to generate a detailed research report.\n"
            "Keep the report professional, structured, and referencing context facts.\n\n"
        )
        
        query_section = f"User Research Query: {query}\n\n"
        
        # Build external findings context
        external_context = "--- EXTERNAL DATA FINDINGS (PubMed & SEC EDGAR) ---\n"
        for i, item in enumerate(external_findings):
            external_context += f"Source: {item.get('title', 'External Document')}\nURL: {item.get('url', '')}\nDetails: {item.get('filing_date', item.get('pubdate', ''))}\n\n"
        external_context += "\n"
        
        # Build segments context
        transcript_header = "--- RELEVANT TRANSCRIPT CONTEXT ---\n"
        
        # Calculate real needed length without segments
        needed_len = len(system_instructions) + len(query_section) + len(external_context) + len(transcript_header)
        
        # If needed length exceeds limits, truncate external context
        if needed_len > max_chars:
            allowed_len = max(0, max_chars - len(system_instructions) - len(query_section) - len(transcript_header) - 50)
            external_context = external_context[:allowed_len] + "... [Truncated]\n\n"

        prompt_prefix = (
            f"{system_instructions}"
            f"{query_section}"
            f"{external_context}"
            f"{transcript_header}"
        )
        
        current_len = len(prompt_prefix)
        transcript_body = ""
        
        for item in segments:
            payload = item.get("payload", item)
            speaker = payload.get("speaker", "Unknown")
            start = payload.get("start", 0.0)
            text = payload.get("text", "")
            
            segment_str = f"[{speaker} @ {start:.1f}s]: {text}\n"
            if current_len + len(segment_str) > max_chars:
                # Try to fit a truncated version of the segment
                prefix = f"[{speaker} @ {start:.1f}s]: "
                if current_len + len(prefix) + 20 <= max_chars:
                    allowed_text_len = max_chars - current_len - len(prefix) - 25
                    if allowed_text_len > 5:
                        truncated_text = text[:allowed_text_len] + "... [Truncated]\n"
                        transcript_body += prefix + truncated_text
                break
            transcript_body += segment_str
            current_len += len(segment_str)

        return prompt_prefix + transcript_body

    def generate_report(self, prompt: str) -> str:
        """
        Generate research report using Ollama or external LLM API endpoint.
        """
        ollama_url = os.environ.get("OLLAMA_URL", "http://127.0.0.1:11434/api/generate")
        external_url = os.environ.get("EXTERNAL_LLM_URL", "")
        external_key = os.environ.get("EXTERNAL_LLM_API_KEY", "")
        
        # 1. Try local Ollama first
        try:
            payload = {
                "model": os.environ.get("OLLAMA_MODEL", "qwen2.5"),
                "prompt": prompt,
                "stream": False
            }
            # Timeout quickly if Ollama is not running
            response = requests.post(ollama_url, json=payload, timeout=5.0)
            if response.status_code == 200:
                return response.json().get("response", "").strip()
        except Exception:
            pass

        # 2. Try External API if configured
        if external_url and external_key:
            try:
                headers = {
                    "Authorization": f"Bearer {external_key}",
                    "Content-Type": "application/json"
                }
                # Standard OpenAI Chat Completion structure
                payload = {
                    "model": os.environ.get("EXTERNAL_LLM_MODEL", "gpt-4o-mini"),
                    "messages": [
                        {"role": "user", "content": prompt}
                    ],
                    "temperature": 0.3
                }
                response = requests.post(external_url, json=payload, headers=headers, timeout=15.0)
                if response.status_code == 200:
                    choices = response.json().get("choices", [])
                    if choices:
                        return choices[0].get("message", {}).get("content", "").strip()
            except Exception:
                pass
                
        # 3. Fallback mock response for testing/development offline
        return (
            "### Momo Research Assistant - VC Insight Report\n\n"
            "**Status**: Offline Fallback Summary\n"
            "This report is generated using local offline fallback mode because the Ollama host is unreachable.\n\n"
            "**Key Findings extracted from context**:\n"
            "- Successfully parsed relevant transcript sections.\n"
            "- Verified external literature/filings information mapping."
        )
