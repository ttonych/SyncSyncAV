"""Validate public commit identity and staged files, or the history before a push."""
from pathlib import Path
import subprocess
import sys
from privacy import PUBLIC_IDENTITY, check_history, inspect, private_patterns

repo=Path(__file__).resolve().parents[1]
def git(*args):
    return subprocess.check_output(["git","-C",str(repo),*args])

def main():
    rules=private_patterns()
    if sys.argv[1:]==["--history"]:
        check_history(repo,rules)
    elif sys.argv[1:]==["--staged"]:
        for key in ("GIT_AUTHOR_IDENT","GIT_COMMITTER_IDENT"):
            if not git("var",key).startswith(PUBLIC_IDENTITY+b" "):
                raise ValueError("Select the public project identity before committing.")
        paths=git("diff","--cached","--name-only","--diff-filter=ACMRT","-z")
        for raw in filter(None,paths.split(b"\0")):
            name=raw.decode("utf-8")
            inspect("staged path",raw,rules)
            inspect(name,git("show",":"+name),rules)
    else:
        raise ValueError("Use --staged or --history.")

if __name__=="__main__":
    try:
        main()
    except ValueError as error:
        sys.exit(str(error))
