"""Local OCR ground truth, leakage-safe export, and model comparison. Python stdlib only."""
import argparse
import hashlib
import json
import math
import random
import re
import shutil
import struct
import sys
import unicodedata
from pathlib import Path


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def line(text):
    if "\n" in text or "\r" in text or not text.strip():
        raise ValueError("A transcript must contain exactly one nonempty text line.")
    return unicodedata.normalize("NFC", text.strip())


def normalized(text):
    return " ".join(unicodedata.normalize("NFC", text).split())


def transcript(path):
    return line(path.read_text(encoding="utf-8").removesuffix("\n"))


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    temporary.replace(path)


def load(root):
    manifest = root / "samples.json"
    return json.loads(manifest.read_text(encoding="utf-8")) if manifest.exists() else []


def checked(root):
    samples = load(root)
    seen = set()
    for sample in samples:
        identifier = sample["id"]
        if not re.fullmatch(r"sample-[0-9a-f]{20}", identifier) or identifier in seen:
            raise ValueError("Invalid or repeated sample ID.")
        seen.add(identifier)
        if digest(root / (identifier + ".png")) != sample["sha256"]:
            raise ValueError("Image changed after import: " + identifier)
        sample["text"] = transcript(root / (identifier + ".gt.txt"))
        if not sample["group"].strip() or not sample["kind"].strip():
            raise ValueError("Each sample needs a capture-session group and a text kind.")
    return samples


def add(root, image, text, group, kind, reviewed=False):
    text = line(text)
    if not group.strip() or not kind.strip():
        raise ValueError("Group and kind cannot be empty.")
    data = image.read_bytes()
    if data[:8] != b"\x89PNG\r\n\x1a\n" or data[12:16] != b"IHDR" or len(data) < 33:
        raise ValueError("Use a PNG containing one already-prepared text line.")
    width, height = struct.unpack(">II", data[16:24])
    if width < 1 or height < 1:
        raise ValueError("Invalid PNG dimensions.")
    image_hash = hashlib.sha256(data).hexdigest()
    samples = checked(root)
    for previous in samples:
        if previous["sha256"] == image_hash:
            if previous["text"] != text:
                raise ValueError("This image already has a different transcript; use review to correct it.")
            return previous["id"]
    identifier = "sample-" + image_hash[:20]
    root.mkdir(parents=True, exist_ok=True)
    (root / (identifier + ".png")).write_bytes(data)
    (root / (identifier + ".gt.txt")).write_text(text + "\n", encoding="utf-8")
    samples.append(dict(id=identifier, sha256=image_hash, group=group, kind=kind,
                        reviewed=reviewed, width=width, height=height, source=image.name))
    for sample in samples:
        sample.pop("text", None)
    write_json(root / "samples.json", samples)
    return identifier


def review(root, identifier, text):
    text = line(text)
    samples = load(root)
    sample = next((s for s in samples if s["id"] == identifier), None)
    if sample is None:
        raise ValueError("Unknown sample ID.")
    (root / (identifier + ".gt.txt")).write_text(text + "\n", encoding="utf-8")
    sample["reviewed"] = True
    write_json(root / "samples.json", samples)


def components(samples):
    # Same capture session, identical pixels, and identical text must stay together.
    parents = list(range(len(samples)))

    def find(index):
        while parents[index] != index:
            parents[index] = parents[parents[index]]
            index = parents[index]
        return index

    owners = {}
    for index, sample in enumerate(samples):
        for key in [("group", sample["group"]), ("image", sample["sha256"]),
                    ("text", normalized(sample["text"]).casefold())]:
            if key in owners:
                parents[find(index)] = find(owners[key])
            else:
                owners[key] = index
    result = {}
    for index, sample in enumerate(samples):
        result.setdefault(find(index), []).append(sample)
    return list(result.values())


def split(root, output, fraction=0.2, seed=42):
    if not 0 < fraction < 1 or not math.isfinite(fraction):
        raise ValueError("Test fraction must be between zero and one.")
    if output.exists():
        raise ValueError("Export directory already exists; use a new snapshot directory.")
    samples = sorted((s for s in checked(root) if s["reviewed"]), key=lambda s: s["id"])
    groups = components(samples)
    if len(groups) < 2:
        raise ValueError("Need at least two independent groups of reviewed samples.")
    random.Random(seed).shuffle(groups)
    target, test, train = max(1, round(len(samples) * fraction)), [], []
    for group in groups:
        (test if len(test) < target and group is not groups[-1] else train).extend(group)
    if not test or not train:
        raise ValueError("Could not make nonempty train and test sets.")
    manifest = dict(seed=seed, requested_test_fraction=fraction, train=len(train), test=len(test), samples=[])
    for name, members in [("train", train), ("test", test)]:
        destination = output / name
        destination.mkdir(parents=True)
        for sample in members:
            identifier = sample["id"]
            for extension in [".png", ".gt.txt"]:
                shutil.copyfile(root / (identifier + extension), destination / (identifier + extension))
            manifest["samples"].append({**sample, "split": name})
    write_json(output / "split.json", manifest)
    return {"train": len(train), "test": len(test), "independent_groups": len(groups)}


def distance(expected, actual):
    row = list(range(len(actual) + 1))
    for i, left in enumerate(expected, 1):
        next_row = [i]
        for j, right in enumerate(actual, 1):
            next_row.append(min(next_row[-1] + 1, row[j] + 1, row[j - 1] + (left != right)))
        row = next_row
    return row[-1]


def score(dataset, prediction_file):
    report = json.loads(prediction_file.read_text(encoding="utf-8"))
    predictions = {}
    for result in report["Results"]:
        if result["Id"] in predictions:
            raise ValueError("Prediction report contains duplicate IDs.")
        predictions[result["Id"]] = result
    images = sorted(dataset.glob("sample-*.png"))
    if not images or set(predictions) != {p.stem for p in images}:
        raise ValueError("Report and dataset must contain exactly the same samples.")
    errors = characters = exact = 0
    milliseconds, details = [], []
    for image in images:
        result = predictions[image.stem]
        if result["ImageSha256"].lower() != digest(image):
            raise ValueError("Prediction was made from different image pixels.")
        expected = normalized(transcript(image.with_suffix(".gt.txt")))
        actual = normalized(result["Text"])
        elapsed = float(result["Milliseconds"])
        if not math.isfinite(elapsed) or elapsed < 0:
            raise ValueError("Invalid prediction timing.")
        mistakes = distance(expected, actual)
        errors += mistakes
        characters += len(expected)
        exact += expected == actual
        milliseconds.append(elapsed)
        details.append(dict(id=image.stem, expected=expected, actual=actual, errors=mistakes))
    milliseconds.sort()
    return dict(model=report["Model"], model_sha256=report["ModelSha256"], psm=report["Psm"],
                lines=len(images), character_errors=errors, reference_characters=characters,
                character_error_percent=round(100 * errors / characters, 3),
                exact_lines=exact, exact_line_percent=round(100 * exact / len(images), 3),
                mean_milliseconds=round(sum(milliseconds) / len(milliseconds), 3),
                p95_milliseconds=round(milliseconds[math.ceil(len(milliseconds) * .95) - 1], 3),
                details=details)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    for name in ["add", "review", "validate", "split"]:
        command = commands.add_parser(name)
        command.add_argument("--root", type=Path, required=True)
        if name in ["add", "review"]:
            command.add_argument("--text", required=True)
        if name == "add":
            command.add_argument("--image", type=Path, required=True)
            command.add_argument("--group", required=True)
            command.add_argument("--kind", choices=["chat", "loot", "exp", "level"], required=True)
            command.add_argument("--reviewed", action="store_true")
        elif name == "review":
            command.add_argument("--id", required=True)
        elif name == "split":
            command.add_argument("--output", type=Path, required=True)
            command.add_argument("--test-fraction", type=float, default=.2)
            command.add_argument("--seed", type=int, default=42)
    command = commands.add_parser("score")
    command.add_argument("--dataset", type=Path, required=True)
    command.add_argument("--predictions", type=Path, required=True)
    command.add_argument("--output", type=Path)
    args = parser.parse_args()
    if args.command == "add":
        print(add(args.root, args.image, args.text, args.group, args.kind, args.reviewed))
    elif args.command == "review":
        review(args.root, args.id, args.text)
    elif args.command == "validate":
        samples = checked(args.root)
        print(json.dumps(dict(total=len(samples), reviewed=sum(s["reviewed"] for s in samples))))
    elif args.command == "split":
        print(json.dumps(split(args.root, args.output, args.test_fraction, args.seed)))
    else:
        result = score(args.dataset, args.predictions)
        if args.output:
            write_json(args.output, result)
        print(json.dumps({k: v for k, v in result.items() if k != "details"}, indent=2))


if __name__ == "__main__":
    try:
        main()
    except (ValueError, KeyError, OSError) as error:
        sys.exit(str(error))
