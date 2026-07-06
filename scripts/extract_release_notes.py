import os
import re

def main():
    changelog_path = "CHANGELOG.md"
    output_path = "Output/release_notes_beta1.md"
    
    if not os.path.exists(changelog_path):
        print(f"Error: {changelog_path} not found.")
        return

    with open(changelog_path, "r", encoding="utf-8") as f:
        content = f.read()

    # Match starting from ## [5.0.0-beta1] to the next ## heading
    match = re.search(r'(## \[5\.0\.0-beta1\].*?)(?=^## \[|\Z)', content, re.DOTALL | re.MULTILINE)
    
    if match:
        release_notes = match.group(1).strip()
        os.makedirs(os.path.dirname(output_path), exist_ok=True)
        with open(output_path, "w", encoding="utf-8") as out:
            out.write(release_notes)
        print(f"Extracted release notes successfully to {output_path}")
    else:
        print("Error: Could not find ## [5.0.0-beta1] section in CHANGELOG.md")

if __name__ == "__main__":
    main()
