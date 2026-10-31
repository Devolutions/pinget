CREATE TABLE metadata(name TEXT PRIMARY KEY NOT NULL,value TEXT NOT NULL) WITHOUT ROWID;
CREATE TABLE packages(
    rowid INTEGER PRIMARY KEY,
    id TEXT NOT NULL,
    name TEXT NOT NULL,
    moniker TEXT,
    latest_version TEXT NOT NULL,
    arp_min_version TEXT,
    arp_max_version TEXT,
    hash BLOB
);
CREATE TABLE tags2(rowid INTEGER PRIMARY KEY,tag TEXT NOT NULL);
CREATE TABLE tags2_map(tag INTEGER NOT NULL,package INTEGER NOT NULL,PRIMARY KEY(tag,package)) WITHOUT ROWID;
CREATE TABLE commands2(rowid INTEGER PRIMARY KEY,command TEXT NOT NULL);
CREATE TABLE commands2_map(command INTEGER NOT NULL,package INTEGER NOT NULL,PRIMARY KEY(command,package)) WITHOUT ROWID;
CREATE TABLE pfns2(pfn TEXT NOT NULL,package INTEGER NOT NULL,PRIMARY KEY(pfn,package)) WITHOUT ROWID;
CREATE TABLE productcodes2(productcode TEXT NOT NULL,package INTEGER NOT NULL,PRIMARY KEY(productcode,package)) WITHOUT ROWID;
CREATE TABLE norm_names2(norm_name TEXT NOT NULL,package INTEGER NOT NULL,PRIMARY KEY(norm_name,package)) WITHOUT ROWID;
CREATE TABLE norm_publishers2(norm_publisher TEXT NOT NULL,package INTEGER NOT NULL,PRIMARY KEY(norm_publisher,package)) WITHOUT ROWID;
CREATE TABLE upgradecodes2(upgradecode TEXT NOT NULL,package INTEGER NOT NULL,PRIMARY KEY(upgradecode,package)) WITHOUT ROWID;
