-- Sanitized schema derived from the first published sales database.
PRAGMA foreign_keys=ON;

CREATE TABLE products (
  id        INTEGER PRIMARY KEY AUTOINCREMENT,
  barcode   TEXT NOT NULL UNIQUE,
  name      TEXT NOT NULL,
  unitPrice INTEGER NOT NULL
);

CREATE TABLE sales (
  id        INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
  code      TEXT NOT NULL UNIQUE,
  createdAt INTEGER NOT NULL,
  total     INTEGER NOT NULL,
  paidCash  INTEGER NOT NULL,
  paidCard  INTEGER NOT NULL,
  change    INTEGER NOT NULL
);

CREATE TABLE sale_lines (
  id        INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
  saleId    INTEGER NOT NULL,
  productId INTEGER,
  barcode   TEXT NOT NULL,
  name      TEXT NOT NULL,
  quantity  INTEGER NOT NULL,
  unitPrice INTEGER NOT NULL,
  lineTotal INTEGER NOT NULL,
  FOREIGN KEY(saleId) REFERENCES sales(id) ON DELETE CASCADE
);

CREATE TABLE fixture_probe(
  fixture_name TEXT PRIMARY KEY NOT NULL,
  fixture_value TEXT NOT NULL
);

INSERT INTO products(id, barcode, name, unitPrice)
VALUES(1, 'FIXTURE-0001', 'Sanitized legacy product', 1234);
INSERT INTO sales(id, code, createdAt, total, paidCash, paidCard, change)
VALUES(1, 'FIXTURE-SALE-0001', 1700000000000, 1234, 1234, 0, 0);
INSERT INTO sale_lines(id, saleId, productId, barcode, name, quantity, unitPrice, lineTotal)
VALUES(1, 1, 1, 'FIXTURE-0001', 'Sanitized legacy product', 1, 1234, 1234);
INSERT INTO fixture_probe(fixture_name, fixture_value)
VALUES('legacy_initial_minimal', 'preserve-initial');
