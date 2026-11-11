#!/usr/bin/env python3
"""Build Renombra's compact offline place index from GeoNames allCountries.zip."""

from __future__ import annotations

import argparse
import sqlite3
import sys
import time
import zipfile
from pathlib import Path


SCHEMA_VERSION = "1"
BATCH_SIZE = 20_000


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("archive", type=Path)
    parser.add_argument("database", type=Path)
    return parser.parse_args()


def build(archive: Path, database: Path) -> None:
    if not archive.is_file():
        raise FileNotFoundError(archive)
    if database.exists():
        raise FileExistsError(f"El destino ya existe: {database}")

    database.parent.mkdir(parents=True, exist_ok=True)
    temporary = database.with_suffix(database.suffix + ".building")
    if temporary.exists():
        temporary.unlink()

    started = time.monotonic()
    read_rows = 0
    kept_rows = 0
    batch: list[tuple[int, str, str, float, float, int, str, str]] = []

    connection = sqlite3.connect(temporary)
    try:
        connection.executescript(
            """
            PRAGMA journal_mode=OFF;
            PRAGMA synchronous=OFF;
            PRAGMA temp_store=FILE;
            PRAGMA cache_size=-65536;
            PRAGMA locking_mode=EXCLUSIVE;
            CREATE TABLE metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL) WITHOUT ROWID;
            CREATE TABLE places (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                ascii_name TEXT NOT NULL,
                latitude REAL NOT NULL,
                longitude REAL NOT NULL,
                population INTEGER NOT NULL,
                country_code TEXT NOT NULL,
                feature_code TEXT NOT NULL
            );
            CREATE VIRTUAL TABLE place_index USING rtree(
                id,
                min_latitude, max_latitude,
                min_longitude, max_longitude
            );
            """
        )
        connection.executemany(
            "INSERT INTO metadata(key, value) VALUES (?, ?)",
            [
                ("schema_version", SCHEMA_VERSION),
                ("source", "GeoNames allCountries.zip"),
                ("license", "CC BY 4.0"),
                ("attribution", "GeoNames (https://www.geonames.org/)"),
            ],
        )

        with zipfile.ZipFile(archive) as zipped:
            with zipped.open("allCountries.txt") as source:
                for raw_line in source:
                    read_rows += 1
                    fields = raw_line.decode("utf-8").rstrip("\n").split("\t")
                    if len(fields) < 19 or fields[6] != "P":
                        continue
                    try:
                        row = (
                            int(fields[0]),
                            fields[1],
                            fields[2],
                            float(fields[4]),
                            float(fields[5]),
                            int(fields[14] or 0),
                            fields[8],
                            fields[7],
                        )
                    except (ValueError, IndexError):
                        continue
                    batch.append(row)
                    if len(batch) >= BATCH_SIZE:
                        insert_batch(connection, batch)
                        kept_rows += len(batch)
                        batch.clear()

                    if read_rows % 500_000 == 0:
                        elapsed = time.monotonic() - started
                        print(
                            f"Leídas {read_rows:,}; localidades {kept_rows + len(batch):,}; {elapsed:.0f} s",
                            flush=True,
                        )

        if batch:
            insert_batch(connection, batch)
            kept_rows += len(batch)

        connection.executemany(
            "INSERT INTO metadata(key, value) VALUES (?, ?)",
            [("input_rows", str(read_rows)), ("place_rows", str(kept_rows))],
        )
        connection.commit()
        connection.execute("PRAGMA optimize")
        connection.commit()
        connection.close()
        temporary.replace(database)
    except Exception:
        connection.close()
        if temporary.exists():
            temporary.unlink()
        raise

    elapsed = time.monotonic() - started
    print(
        f"Índice terminado: {kept_rows:,} localidades, {database.stat().st_size / 1024 / 1024:.1f} MB, {elapsed:.0f} s",
        flush=True,
    )


def insert_batch(connection: sqlite3.Connection, batch: list[tuple]) -> None:
    connection.executemany(
        """INSERT INTO places
           (id, name, ascii_name, latitude, longitude, population, country_code, feature_code)
           VALUES (?, ?, ?, ?, ?, ?, ?, ?)""",
        batch,
    )
    connection.executemany(
        """INSERT INTO place_index
           (id, min_latitude, max_latitude, min_longitude, max_longitude)
           VALUES (?, ?, ?, ?, ?)""",
        ((row[0], row[3], row[3], row[4], row[4]) for row in batch),
    )


def main() -> int:
    args = parse_args()
    build(args.archive, args.database)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exception:
        print(f"ERROR: {exception}", file=sys.stderr)
        raise
