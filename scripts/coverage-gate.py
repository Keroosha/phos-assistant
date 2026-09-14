#!/usr/bin/env python3
"""Coverage gate for Phos.

Parses Cobertura XML produced by coverlet and enforces the project policy:

  - total line coverage >= 90%
  - total branch coverage >= 85%
  - per-project branch coverage >= 90% for Core / Storage / Scheduler

Only source files under `src/` are counted; test projects are ignored.
If no `src/` lines are measured yet (Phase 0 state), the gate reports a
note and passes: there is nothing to cover.

Multiple Cobertura XMLs (one per test project) are merged per source file by
taking the best (maximum) line/branch rate observed, so a line covered by any
test project counts as covered; this avoids double-counting the same file's
lines when more than one test project runs in the same gate invocation.

Usage: coverage-gate.py <cobertura.xml|directory>... [--src-root src] [--strict]
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


class ClassCoverage:
    """Per source-file coverage merged across Cobertura XMLs."""

    def __init__(self) -> None:
        self.lines = 0
        self.line_rate = 0.0
        self.branches = 0
        self.branch_rate = 0.0

    def merge(self, lines: int, line_rate: float, branches: int, branch_rate: float) -> None:
        # The same source file reports the same line/branch count across all
        # test-project XMLs; keep the highest rate (union approximation).
        self.lines = max(self.lines, lines)
        self.line_rate = max(self.line_rate, line_rate)
        self.branches = max(self.branches, branches)
        self.branch_rate = max(self.branch_rate, branch_rate)


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


def parse(xml_path: str, src_root: str) -> dict[str, ClassCoverage]:
    """Parse one Cobertura XML into per-source-file coverage."""
    tree = ET.parse(xml_path)
    root = tree.getroot()
    sources = [s.text or "" for s in root.iter("source")]
    classes: dict[str, ClassCoverage] = {}
    for package in root.iter("package"):
        for cls in package.iter("class"):
            # F# `task`/`async` computation expressions compile into
            # compiler-generated state-machine classes named
            # `<StartupCode$...>...` whose branch attribution is unreliable
            # (coverlet reports dozens of synthetic conditions). Per the repo
            # policy (research-plan v2 §2.8) only generated code is excluded,
            # so skip exactly these classes; hand-written code is still measured.
            if (cls.get("name") or "").startswith("<StartupCode$"):
                continue
            filename = cls.get("filename", "")
            key = resolve_class_path(sources, filename, src_root)
            if src_dir_of(key, src_root) is None:
                continue
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
            cc = classes.setdefault(key, ClassCoverage())
            cc.merge(line_count, line_rate, branch_count, branch_rate)
    return classes


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("targets", nargs="+", help="Cobertura XML file(s) or directory/directories of XML files")
    ap.add_argument("--src-root", default="src", help="source root directory name")
    args = ap.parse_args()

    xmls: list[str] = []
    for target in args.targets:
        if os.path.isdir(target):
            xmls.extend(sorted(glob.glob(os.path.join(target, "**", "*.xml"), recursive=True)))
        elif os.path.isfile(target):
            xmls.append(target)
        # Non-existent paths (e.g. a shell glob artifact for a test project
        # with no TestResults) are silently skipped.

    # Merge coverage per source file across all XMLs.
    classes: dict[str, ClassCoverage] = {}
    for xml in xmls:
        for key, cc in parse(xml, args.src_root).items():
            if key not in classes:
                classes[key] = ClassCoverage()
            classes[key].merge(cc.lines, cc.line_rate, cc.branches, cc.branch_rate)

    # Aggregate into per-project totals.
    projects: dict[str, Project] = {}
    for key, cc in classes.items():
        proj_dir = src_dir_of(key, args.src_root)
        if proj_dir is None:
            continue
        proj = projects.setdefault(proj_dir, Project(proj_dir))
        proj.lines += cc.lines
        proj.lines_covered += cc.line_rate * cc.lines
        proj.branches += cc.branches
        proj.branches_covered += cc.branch_rate * cc.branches

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
        # Match project directory names like "Phos.Core" against required
        # short names ("Core", "Storage", "Scheduler") by dotted segment.
        if any(p in name.split(".") for p in BRANCH_REQUIRED_PROJECTS):
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
