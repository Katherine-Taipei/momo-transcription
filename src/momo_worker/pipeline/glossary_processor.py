import os
import json
import re

class GlossaryProcessor:
    def __init__(self, glossaries_dir: str):
        self.glossaries_dir = glossaries_dir
        self._cache = {}  # Cache of filename -> (compiled_pattern, mapping_dict)

    def _get_glossary(self, filename: str):
        if filename in self._cache:
            return self._cache[filename]

        filepath = os.path.join(self.glossaries_dir, filename)
        if not os.path.exists(filepath):
            return None

        try:
            with open(filepath, "r", encoding="utf-8") as f:
                data = json.load(f)

            # Sort keys by length descending to match longer phrases first
            sorted_keys = sorted(data.keys(), key=len, reverse=True)
            if not sorted_keys:
                return None

            # Escape keys for regex safety
            escaped_keys = [re.escape(k) for k in sorted_keys]

            # Match if not preceded/followed by letters/digits (supports both ASCII and CJK characters)
            pattern_str = r"(?<![a-zA-Z0-9])(" + "|".join(escaped_keys) + r")(?![a-zA-Z0-9])"
            pattern = re.compile(pattern_str, re.IGNORECASE)

            # Normalize keys for lookup mapping
            normalized_map = {k.lower(): v for k, v in data.items()}

            self._cache[filename] = (pattern, normalized_map)
            return self._cache[filename]
        except Exception as e:
            import sys
            sys.stderr.write(f"[Glossary Warning] Failed to load glossary {filename}: {e}\n")
            return None

    def process_text(self, text: str, glossary_filenames: list) -> str:
        if not text or not glossary_filenames:
            return text

        for filename in glossary_filenames:
            glossary_data = self._get_glossary(filename)
            if not glossary_data:
                continue

            pattern, mapping = glossary_data

            def replace_match(match):
                matched_text = match.group(0).lower()
                return mapping.get(matched_text, match.group(0))

            text = pattern.sub(replace_match, text)

        return text
