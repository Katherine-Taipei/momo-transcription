import os
import glob
import re
from datetime import datetime
import subprocess

def main():
    # Define paths
    local_app_data = os.environ.get("LOCALAPPDATA", "")
    if not local_app_data:
        local_app_data = os.path.expanduser("~\\AppData\\Local")
    
    logs_dir = os.path.join(local_app_data, "Momo", "logs")
    report_dir = "beta-daily-report"
    os.makedirs(report_dir, exist_ok=True)

    today = datetime.now().strftime("%Y-%m-%d")
    report_path = os.path.join(report_dir, f"{today}.txt")

    print(f"Aggregating logs from {logs_dir}...")
    log_pattern = os.path.join(logs_dir, "*.log")
    log_files = glob.glob(log_pattern)

    summary_lines = []
    seen_messages = set()

    for log_file in log_files:
        filename = os.path.basename(log_file)
        try:
            with open(log_file, "r", encoding="utf-8", errors="ignore") as f:
                for line in f:
                    if "ERROR" in line.upper() or "WARN" in line.upper() or "EXCEPTION" in line.upper():
                        clean_line = line.strip()
                        # Deduplicate using normalized messages
                        message_key = re.sub(r'\d{4}-\d{2}-\d{2}[T\s]\d{2}:\d{2}:\d{2}(\.\d+)?', '', clean_line)
                        message_key = re.sub(r'0x[0-9a-fA-F]+', 'HEX', message_key)
                        if message_key not in seen_messages:
                            seen_messages.add(message_key)
                            summary_lines.append(f"[{filename}] {clean_line}")
        except Exception as e:
            print(f"Error reading {log_file}: {e}")

    if not summary_lines:
        summary_content = f"Daily Log Summary for {today}\nNo ERROR or WARN entries found."
    else:
        summary_content = f"Daily Log Summary for {today}\n" + "="*50 + "\n" + "\n".join(summary_lines)

    with open(report_path, "w", encoding="utf-8") as f:
        f.write(summary_content)

    print(f"Daily log summary saved to {report_path}")

    # Commit and push to git branch beta-qa
    try:
        subprocess.run(["git", "add", report_path], check=True)
        subprocess.run(["git", "commit", "-m", f"docs(daily-report): log summary for {today}"], check=True)
        subprocess.run(["git", "push", "origin", "beta-qa"], check=True)
        print("Log summary successfully pushed to beta-qa")
    except Exception as e:
        print(f"Failed to push daily report to git: {e}")

if __name__ == "__main__":
    main()
