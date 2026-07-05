#!/usr/bin/env python3
import os
import sys
import argparse
import subprocess

# Ensure we can import utils.db_helper if run from the worker folder
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

# Force UTF-8 encoding for stdout and stderr to prevent Unicode encoding issues on Windows
if sys.platform.startswith("win"):
    try:
        sys.stdout.reconfigure(encoding='utf-8')
        sys.stderr.reconfigure(encoding='utf-8')
    except AttributeError:
        pass  # In case of older python or restricted environments

from utils.db_helper import DbHelper

def find_momo_app():
    # Search locations
    script_dir = os.path.dirname(os.path.abspath(__file__))
    project_root = os.path.abspath(os.path.join(script_dir, "..", ".."))
    
    locations = [
        # Package / installed layout (same folder as worker)
        os.path.join(script_dir, "Momo.App.exe"),
        os.path.join(script_dir, "Momo.App.dll"),
        # Dev layout
        os.path.join(project_root, "src", "Momo.App", "bin", "Debug", "net8.0-windows", "Momo.App.exe"),
        os.path.join(project_root, "src", "Momo.App", "bin", "Debug", "net8.0-windows", "Momo.App.dll"),
        os.path.join(project_root, "src", "Momo.App", "bin", "Release", "net8.0-windows", "Momo.App.exe"),
        os.path.join(project_root, "src", "Momo.App", "bin", "Release", "net8.0-windows", "Momo.App.dll"),
    ]
    
    for path in locations:
        if os.path.exists(path):
            return path
            
    return None

def handle_rebuild_index(args):
    if not os.path.exists(args.db):
        print(f"Error: Database file '{args.db}' does not exist.", file=sys.stderr)
        sys.exit(1)
        
    try:
        db = DbHelper(args.db)
        inserted, duration = db.rebuild_transcript_index(args.id)
        print(f"Successfully rebuilt index: {inserted} tokens indexed in {duration:.2f}ms.")
    except Exception as e:
        print(f"Error rebuilding index: {e}", file=sys.stderr)
        sys.exit(1)

def handle_export_docx(args):
    app_path = find_momo_app()
    if not app_path:
        print("Error: Could not locate Momo.App executable or DLL for export-docx.", file=sys.stderr)
        sys.exit(1)
        
    cmd = []
    if app_path.endswith(".dll"):
        cmd = ["dotnet", app_path, "export-docx", "--db", args.db, "--id", args.id, "--out", args.out]
    else:
        cmd = [app_path, "export-docx", "--db", args.db, "--id", args.id, "--out", args.out]
        
    print(f"Running backend exporter: {' '.join(cmd)}")
    result = subprocess.run(cmd, capture_output=True, text=True)
    
    if result.returncode == 0:
        print(result.stdout.strip())
        print(f"Successfully exported transcript '{args.id}' to '{args.out}'.")
    else:
        print(f"Error exporting DOCX:\nSTDOUT:\n{result.stdout}\nSTDERR:\n{result.stderr}", file=sys.stderr)
        sys.exit(result.returncode)

def main():
    parser = argparse.ArgumentParser(
        description="momoctl - Momo Transcription Platform Command Line Tool",
        formatter_class=argparse.RawDescriptionHelpFormatter
    )
    parser.add_argument("--version", action="version", version="momoctl 5.0.0-alpha2")
    subparsers = parser.add_subparsers(dest="command", required=False, help="Subcommand to execute")
    
    # rebuild-index
    rebuild_parser = subparsers.add_parser("rebuild-index", help="Rebuild SQLite transcript search index")
    rebuild_parser.add_argument("--db", required=True, help="Path to the SQLite database")
    rebuild_parser.add_argument("--id", required=True, help="Transcript ID")
    
    # export-docx
    export_parser = subparsers.add_parser("export-docx", help="Export transcript to DOCX with track changes")
    export_parser.add_argument("--db", required=True, help="Path to the SQLite database")
    export_parser.add_argument("--id", required=True, help="Transcript ID")
    export_parser.add_argument("--out", required=True, help="Output DOCX file path")
    
    args = parser.parse_args()
    
    if not args.command:
        parser.print_help()
        sys.exit(0)
        
    if args.command == "rebuild-index":
        handle_rebuild_index(args)
    elif args.command == "export-docx":
        handle_export_docx(args)

if __name__ == "__main__":
    main()
