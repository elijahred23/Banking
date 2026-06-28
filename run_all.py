#!/usr/bin/env python3
"""Build and run every executable C# project in this repository."""

from __future__ import annotations

import argparse
import os
from pathlib import Path
import signal
import subprocess
import sys
import time
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parent
SOLUTION = ROOT / "Banking.slnx"
WEB_PROJECT = ROOT / "src" / "Banking.Web" / "Banking.Web.csproj"


def is_runnable(project: Path) -> bool:
    """Return whether a project produces an application that dotnet can run."""
    root = ET.parse(project).getroot()
    sdk = root.attrib.get("Sdk", "")
    output_types = {
        (element.text or "").strip().lower()
        for element in root.iter()
        if element.tag.rsplit("}", 1)[-1] == "OutputType"
    }
    return sdk.endswith((".Web", ".Worker")) or bool(
        output_types.intersection({"exe", "winexe"})
    )


def find_projects() -> list[Path]:
    projects = [project for project in ROOT.rglob("*.csproj") if is_runnable(project)]
    # The web app initializes the database, so it must start before the workers.
    return sorted(projects, key=lambda project: (project != WEB_PROJECT, str(project)))


def stop_processes(processes: list[subprocess.Popen[bytes]]) -> None:
    for process in processes:
        if process.poll() is not None:
            continue
        if os.name == "nt":
            process.terminate()
        else:
            os.killpg(process.pid, signal.SIGTERM)

    deadline = time.monotonic() + 5
    for process in processes:
        if process.poll() is not None:
            continue
        try:
            process.wait(timeout=max(0, deadline - time.monotonic()))
        except subprocess.TimeoutExpired:
            if os.name == "nt":
                process.kill()
            else:
                os.killpg(process.pid, signal.SIGKILL)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Build and run all executable C# projects."
    )
    parser.add_argument(
        "-c", "--configuration", default="Debug", help="Build configuration (default: Debug)"
    )
    parser.add_argument(
        "--no-build", action="store_true", help="Skip the initial solution build"
    )
    parser.add_argument(
        "--list", action="store_true", help="List the projects that would run and exit"
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    projects = find_projects()
    if not projects:
        print("No executable C# projects found.", file=sys.stderr)
        return 1

    if args.list:
        for project in projects:
            print(project.relative_to(ROOT))
        return 0

    if not args.no_build:
        build = subprocess.run(
            ["dotnet", "build", str(SOLUTION), "-c", args.configuration], cwd=ROOT
        )
        if build.returncode != 0:
            return build.returncode

    processes: list[subprocess.Popen[bytes]] = []
    popen_options: dict[str, object] = {}
    if os.name == "nt":
        popen_options["creationflags"] = subprocess.CREATE_NEW_PROCESS_GROUP
    else:
        popen_options["start_new_session"] = True

    try:
        for index, project in enumerate(projects):
            name = project.stem
            print(f"Starting {name}...", flush=True)
            process = subprocess.Popen(
                [
                    "dotnet",
                    "run",
                    "--project",
                    str(project),
                    "-c",
                    args.configuration,
                    "--no-build",
                ],
                cwd=ROOT,
                **popen_options,
            )
            processes.append(process)

            # Give the web app time to initialize the database before workers connect.
            if index == 0 and project == WEB_PROJECT:
                time.sleep(2)
                if process.poll() is not None:
                    return process.returncode or 1

        while True:
            for project, process in zip(projects, processes):
                return_code = process.poll()
                if return_code is not None:
                    print(
                        f"{project.stem} exited with code {return_code}; stopping all projects.",
                        file=sys.stderr,
                    )
                    return return_code or 1
            time.sleep(0.25)
    except KeyboardInterrupt:
        print("\nStopping all projects...", flush=True)
        return 130
    finally:
        stop_processes(processes)


if __name__ == "__main__":
    raise SystemExit(main())
