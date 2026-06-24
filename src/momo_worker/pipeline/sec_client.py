import os
import time
import requests
from typing import List, Dict, Any

class SECClient:
    def __init__(self, user_agent: str = ""):
        # Default user-agent compliant with SEC guidelines
        self.user_agent = user_agent or os.environ.get(
            "SEC_USER_AGENT", 
            "MomoResearchBot/1.0 (contact@company.com)"
        )
        self.headers = {
            "User-Agent": self.user_agent,
            "Accept-Encoding": "gzip, deflate"
        }
        # SEC rate limits: 10 requests per second maximum
        self.rate_limit_delay = 0.12
        self.last_request_time = 0.0
        self._cik_map = None  # Lazy cache for ticker-CIK map

    def _throttle(self):
        now = time.time()
        elapsed = now - self.last_request_time
        if elapsed < self.rate_limit_delay:
            time.sleep(self.rate_limit_delay - elapsed)
        self.last_request_time = time.time()

    def _request_with_retry(self, url: str, max_retries: int = 3) -> Any:
        retries = 0
        backoff = 1.0
        while True:
            self._throttle()
            try:
                response = requests.get(url, headers=self.headers, timeout=10.0)
                if response.status_code == 200:
                    return response.json()
                elif response.status_code in (429, 500, 502, 503, 504):
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

    def _load_cik_map(self):
        if self._cik_map is not None:
            return
            
        url = "https://www.sec.gov/files/company_tickers.json"
        try:
            data = self._request_with_retry(url)
            self._cik_map = {}
            for item in data.values():
                cik = str(item["cik_str"]).zfill(10)
                ticker = str(item["ticker"]).upper()
                title = str(item["title"]).lower()
                self._cik_map[ticker] = {"cik": cik, "title": title}
        except Exception:
            self._cik_map = {}

    def get_cik(self, query: str) -> str:
        """
        Get 10-digit padded CIK string for a ticker or company name.
        """
        if not query:
            return ""
            
        # If query is numeric, assume it is CIK already and pad it
        if query.isdigit():
            return query.zfill(10)
            
        self._load_cik_map()
        query_upper = query.upper()
        
        # 1. Direct Ticker Lookup
        if query_upper in self._cik_map:
            return self._cik_map[query_upper]["cik"]
            
        # 2. Company Name Lookup
        query_lower = query.lower()
        for ticker, info in self._cik_map.items():
            if query_lower in info["title"]:
                return info["cik"]
                
        return ""

    def get_recent_filings(self, query: str, limit: int = 5) -> List[Dict[str, Any]]:
        """
        Retrieve recent filings for a company ticker/CIK/name.
        """
        cik = self.get_cik(query)
        if not cik:
            return []
            
        url = f"https://data.sec.gov/submissions/CIK{cik}.json"
        try:
            data = self._request_with_retry(url)
            filings = data.get("filings", {}).get("recent", {})
            if not filings:
                return []
                
            recent_list = []
            num_filings = len(filings.get("accessionNumber", []))
            
            for i in range(min(num_filings, limit)):
                acc_num = filings["accessionNumber"][i]
                acc_num_no_dashes = acc_num.replace("-", "")
                primary_doc = filings["primaryDocument"][i]
                form = filings["form"][i]
                filing_date = filings["filingDate"][i]
                
                filing_url = f"https://www.sec.gov/Archives/edgar/data/{int(cik)}/{acc_num_no_dashes}/{primary_doc}"
                
                recent_list.append({
                    "cik": cik,
                    "accession_number": acc_num,
                    "form": form,
                    "filing_date": filing_date,
                    "title": f"SEC Form {form} (Filed: {filing_date})",
                    "url": filing_url
                })
            return recent_list
        except Exception:
            return []
