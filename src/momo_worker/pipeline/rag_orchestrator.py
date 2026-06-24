import os
import uuid
from typing import List, Dict, Any
from qdrant_client import QdrantClient
from qdrant_client.models import Distance, VectorParams, PointStruct
from sentence_transformers import SentenceTransformer

class RagOrchestrator:
    def __init__(self):
        self._model = None
        self._client = None
        self.collection_name = "momo_transcripts"
        self.vector_size = 384  # all-MiniLM-L6-v2 dimension

    @property
    def model(self):
        if self._model is None:
            # SentenceTransformer handles downloading/caching of local model offline
            self._model = SentenceTransformer("all-MiniLM-L6-v2")
        return self._model

    @property
    def client(self):
        if self._client is None:
            host = os.environ.get("QDRANT_HOST", "localhost")
            port = int(os.environ.get("QDRANT_PORT", 6333))
            try:
                # Try Docker service
                client = QdrantClient(host=host, port=port, timeout=1.0)
                client.get_collections()
                self._client = client
            except Exception:
                # Fallback to in-memory client
                self._client = QdrantClient(":memory:")
        return self._client

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
            
            # Generate dense embedding vector
            vector = self.model.encode(text).tolist()
            
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

    def query_segments(self, project_id: str, query: str, limit: int = 5) -> List[Dict[str, Any]]:
        self.ensure_collection()
        vector = self.model.encode(query).tolist()
        
        # Filter results by project_id
        from qdrant_client.models import Filter, FieldCondition, MatchValue
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
