import os
import time
import requests
from typing import List, Dict, Any

class PubMedClient:
    def __init__(self):
        self.api_key = os.environ.get("PUBMED_API_KEY", "")
        # NCBI rate limits: 3 req/s without key, 10 req/s with key
        self.rate_limit_delay = 0.11 if self.api_key else 0.35
        self.last_request_time = 0.0
        self.base_url = "https://eutils.ncbi.nlm.nih.gov/entrez/eutils"

    def _throttle(self):
        now = time.time()
        elapsed = now - self.last_request_time
        if elapsed < self.rate_limit_delay:
            time.sleep(self.rate_limit_delay - elapsed)
        self.last_request_time = time.time()

    def _request_with_retry(self, url: str, params: Dict[str, Any], max_retries: int = 3) -> Any:
        retries = 0
        backoff = 1.0
        
        # Add API Key if configured
        if self.api_key:
            params["api_key"] = self.api_key

        while True:
            self._throttle()
            try:
                response = requests.get(url, params=params, timeout=10.0)
                if response.status_code == 200:
                    return response.json()
                elif response.status_code in (429, 500, 502, 503, 504):
                    # Rate limit or temporary server error
                    retries += 1
                    if retries > max_retries:
                        response.raise_for_status()
                    time.sleep(backoff)
                    backoff *= 2.0
                else:
                    response.raise_for_status()
            except Exception as e:
                retries += 1
                if retries > max_retries:
                    raise e
                time.sleep(backoff)
                backoff *= 2.0

    def search(self, term: str, limit: int = 5) -> List[str]:
        """
        Search PubMed for term, return list of PMIDs.
        """
        if not term:
            return []
            
        url = f"{self.base_url}/esearch.fcgi"
        params = {
            "db": "pubmed",
            "term": term,
            "retmode": "json",
            "retmax": limit
        }
        try:
            data = self._request_with_retry(url, params)
            id_list = data.get("esearchresult", {}).get("idlist", [])
            return id_list
        except Exception:
            return []

    def fetch_summaries(self, pmids: List[str]) -> List[Dict[str, Any]]:
        """
        Fetch esummary for a list of PMIDs.
        """
        if not pmids:
            return []
            
        url = f"{self.base_url}/esummary.fcgi"
        params = {
            "db": "pubmed",
            "id": ",".join(pmids),
            "retmode": "json"
        }
        try:
            data = self._request_with_retry(url, params)
            results = data.get("result", {})
            
            extracted = []
            for pmid in pmids:
                doc = results.get(pmid, {})
                if not doc or "title" not in doc:
                    continue
                extracted.append({
                    "id": pmid,
                    "title": doc.get("title", ""),
                    "pubdate": doc.get("pubdate", ""),
                    "source": doc.get("source", ""),
                    "url": f"https://pubmed.ncbi.nlm.nih.gov/{pmid}/"
                })
            return extracted
        except Exception:
            return []

    def get_details(self, term: str, limit: int = 5) -> List[Dict[str, Any]]:
        """
        High-level method returning search results directly.
        """
        pmids = self.search(term, limit)
        return self.fetch_summaries(pmids)
