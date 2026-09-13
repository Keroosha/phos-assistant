#!/usr/bin/env python3
"""Coverage gate for Phos.

Parses Cobertura XML produced by coverlet and enforces the project policy:

  - total line coverage >= 90%
  - total branch coverage >= 85%
  - per-project branch coverage >= 90% for Core / Storage / Scheduler

Only source files under `src/` are counted; test projects are ignored.
If no `src/` lines are measured yet (Phase 0 state), the gate reports a
note and passes: there is nothing to cover.

Usage: coverage-gate.py <cobertura.xml|directory> [--src-root src] [--strict]
"""
from __future__ import annotations

import argparse
import glob
import os
import sys
import xml.etree.ElementTree as ET

LINE_TOTAL = 0.90
BRANCH_TOTAL = 0.85
BRANCH_PER_PROJECT = 0.90
BRANCH_REQUIRED_PROJECTS = ("Core", "Storage", "Scheduler")


class Project:
    def __init__(self, name: str):
        self.name = name
        self.lines = 0
        self.lines_covered = 0.0
        self.branches = 0
        self.branches_covered = 0.0


def src_dir_of(filename: str, src_root: str) -> str | None:
    """Map a source file path to its project directory under src_root."""
    normalized = filename.replace("\\", "/")
    marker = f"/{src_root}/"
    idx = normalized.find(marker)
    if idx < 0:
        return None
    rest = normalized[idx + len(marker):]
    parts = rest.split("/")
    # parts[0] is the project directory (e.g. "Phos.Core")
    return parts[0] if parts else None


def resolve_class_path(sources: list[str], filename: str, src_root: str) -> str:
    """Resolve a Cobertura class filename to a path containing src_root.

    Coverlet emits filenames relative to each `<sources>` entry (e.g.
    ``OutboxStateMachine.fs`` with ``<source>/abs/src/Phos.Core/</source>``),
    not repo-root-relative paths. Join with every source entry and pick the
    first candidate that lands under ``src_root``; absolute/root-relative
    filenames are used as-is.
    """
    normalized = filename.replace("\\", "/")
    if f"/{src_root}/" in normalized:
        return normalized
    for source in sources:
        candidate = os.path.join(source, filename).replace("\\", "/")
        if f"/{src_root}/" in candidate:
            return candidate
    return os.path.join(sources[0], filename) if sources else filename


def collect(xml_path: str, src_root: str) -> dict[str, Project]:
    tree = ET.parse(xml_path)
    root = tree.getroot()
    sources = [s.text or "" for s in root.iter("source")]
    projects: dict[str, Project] = {}
    for package in root.iter("package"):
        for cls in package.iter("class"):
            filename = cls.get("filename", "")
            proj_dir = src_dir_of(resolve_class_path(sources, filename, src_root), src_root)
            if proj_dir is None:
                continue
            proj = projects.setdefault(proj_dir, Project(proj_dir))
            line_count = 0
            line_rate = float(cls.get("line-rate", "0"))
            branch_count = 0
            branch_rate = float(cls.get("branch-rate", "0"))
            for line in cls.iter("line"):
                line_count += 1
                # Coverlet writes the attribute capitalized ("True"/"False");
                # the Cobertura spec uses lowercase.
                if (line.get("branch") or "false").lower() == "true":
                    branch_count += 1
            proj.lines += line_count
            proj.lines_covered += line_rate * line_count
            proj.branches += branch_count
            proj.branches_covered += branch_rate * branch_count
    return projects


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("target", help="Cobertura XML file or directory of XML files")
    ap.add_argument("--src-root", default="src", help="source root directory name")
    args = ap.parse_args()

    if os.path.isdir(args.target):
        xmls = sorted(glob.glob(os.path.join(args.target, "**", "*.xml"), recursive=True))
    else:
        xmls = [args.target]

    projects: dict[str, Project] = {}
    for xml in xmls:
        for name, proj in collect(xml, args.src_root).items():
            if name not in projects:
                projects[name] = Project(name)
            projects[name].lines += proj.lines
            projects[name].lines_covered += proj.lines_covered
            projects[name].branches += proj.branches
            projects[name].branches_covered += proj.branches_covered

    total_lines = sum(p.lines for p in projects.values())
    total_lines_covered = sum(p.lines_covered for p in projects.values())
    total_branches = sum(p.branches for p in projects.values())
    total_branches_covered = sum(p.branches_covered for p in projects.values())

    if total_lines == 0:
        print("coverage-gate: no src/ lines measured (Phase 0 state) — nothing to cover")
        return 0

    line_rate = total_lines_covered / total_lines
    branch_rate = total_branches_covered / total_branches if total_branches else 1.0

    failures = []
    if line_rate < LINE_TOTAL:
        failures.append(f"total line coverage {line_rate:.1%} < {LINE_TOTAL:.0%}")
    if total_branches > 0 and branch_rate < BRANCH_TOTAL:
        failures.append(f"total branch coverage {branch_rate:.1%} < {BRANCH_TOTAL:.0%}")

    for name, proj in sorted(projects.items()):
        if any(proj.name.startswith(p) for p in BRANCH_REQUIRED_PROJECTS):
            rate = proj.branches_covered / proj.branches if proj.branches else 1.0
            print(f"  {proj.name}: branch {rate:.1%} ({proj.branches_covered:.0f}/{proj.branches})")
            if proj.branches > 0 and rate < BRANCH_PER_PROJECT:
                failures.append(f"{proj.name} branch coverage {rate:.1%} < {BRANCH_PER_PROJECT:.0%}")

    print(f"  total: line {line_rate:.1%} ({total_lines_covered:.0f}/{total_lines}), "
          f"branch {branch_rate:.1%} ({total_branches_covered:.0f}/{total_branches})")
    for name, proj in sorted(projects.items()):
        lr = proj.lines_covered / proj.lines if proj.lines else 1.0
        br = proj.branches_covered / proj.branches if proj.branches else 1.0
        print(f"  {name}: line {lr:.1%}, branch {br:.1%}")

    if failures:
        print("coverage-gate: FAIL")
        for f in failures:
            print(f"  - {f}")
        return 1

    print("coverage-gate: PASS")
    return 0


if __name__ == "__main__":
    sys.exit(main())
