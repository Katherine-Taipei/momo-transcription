import sys
import os
import unittest
from unittest.mock import patch, MagicMock

# Adjust path to import local modules
sys.path.append(os.path.abspath(os.path.join(os.path.dirname(__file__), "..")))

from pipeline.pubmed_client import PubMedClient
from pipeline.sec_client import SECClient

class TestPhase3P3_2(unittest.TestCase):
    @patch("requests.get")
    def test_PubMed_Search_Fetch(self, mock_get):
        # 1. Setup Mock responses
        mock_search_response = MagicMock()
        mock_search_response.status_code = 200
        mock_search_response.json.return_value = {
            "esearchresult": {
                "idlist": ["12345", "67890"]
            }
        }
        
        mock_summary_response = MagicMock()
        mock_summary_response.status_code = 200
        mock_summary_response.json.return_value = {
            "result": {
                "12345": {
                    "title": "Clinical trials of Tirzepatide",
                    "pubdate": "2023 Nov 23",
                    "source": "New Engl J Med"
                },
                "67890": {
                    "title": "Venture capital in biotechnology",
                    "pubdate": "2024 Jan 15",
                    "source": "Nature Biotech"
                }
            }
        }
        
        # Side effect to return search first, then summary
        mock_get.side_effect = [mock_search_response, mock_summary_response]

        client = PubMedClient()
        
        # Test search
        pmids = client.search("Tirzepatide", limit=2)
        self.assertEqual(pmids, ["12345", "67890"])
        
        # Test fetch summaries
        details = client.fetch_summaries(pmids)
        self.assertEqual(len(details), 2)
        
        self.assertEqual(details[0]["id"], "12345")
        self.assertEqual(details[0]["title"], "Clinical trials of Tirzepatide")
        self.assertEqual(details[0]["pubdate"], "2023 Nov 23")
        self.assertEqual(details[0]["url"], "https://pubmed.ncbi.nlm.nih.gov/12345/")
        
        self.assertEqual(details[1]["id"], "67890")
        self.assertEqual(details[1]["title"], "Venture capital in biotechnology")
        self.assertEqual(details[1]["url"], "https://pubmed.ncbi.nlm.nih.gov/67890/")

    @patch("requests.get")
    def test_SEC_Filing_Fetch(self, mock_get):
        # 1. Setup Mock responses
        mock_tickers_response = MagicMock()
        mock_tickers_response.status_code = 200
        mock_tickers_response.json.return_value = {
            "0": {"cik_str": 320193, "ticker": "AAPL", "title": "Apple Inc."},
            "1": {"cik_str": 789019, "ticker": "MSFT", "title": "MICROSOFT CORP"}
        }
        
        mock_submissions_response = MagicMock()
        mock_submissions_response.status_code = 200
        mock_submissions_response.json.return_value = {
            "cik": "0000320193",
            "entityType": "operating",
            "sic": "3571",
            "name": "Apple Inc.",
            "tickers": ["AAPL"],
            "filings": {
                "recent": {
                    "accessionNumber": ["0000320193-23-000106", "0000320193-23-000097"],
                    "form": ["10-Q", "10-K"],
                    "filingDate": ["2023-11-03", "2023-10-31"],
                    "reportDate": ["2023-09-30", "2023-09-30"],
                    "primaryDocument": ["aapl-20230930.htm", "aapl-20230930_10k.htm"]
                }
            }
        }
        
        # Side effect: first call loads tickers mapping, second fetches submissions
        mock_get.side_effect = [mock_tickers_response, mock_submissions_response]
        
        client = SECClient(user_agent="TestBot/1.0 (test@company.com)")
        
        # Test CIK translation
        cik = client.get_cik("AAPL")
        self.assertEqual(cik, "0000320193")
        
        # Test get filings
        filings = client.get_recent_filings("AAPL", limit=2)
        self.assertEqual(len(filings), 2)
        
        self.assertEqual(filings[0]["form"], "10-Q")
        self.assertEqual(filings[0]["filing_date"], "2023-11-03")
        self.assertEqual(
            filings[0]["url"], 
            "https://www.sec.gov/Archives/edgar/data/320193/000032019323000106/aapl-20230930.htm"
        )
        
        self.assertEqual(filings[1]["form"], "10-K")
        self.assertEqual(
            filings[1]["url"], 
            "https://www.sec.gov/Archives/edgar/data/320193/000032019323000097/aapl-20230930_10k.htm"
        )

if __name__ == "__main__":
    unittest.main()
