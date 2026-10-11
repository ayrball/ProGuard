# Database schema upgrades

ProGuard keeps rules and settings in one SQLite database (`%ProgramData%\ProGuard\proguard.db`,
or `PROGUARD_DB_PATH`) that the engine and the UI share. There are no EF migrations. Instead,
every place that opens the database calls `db.EnsureSchema()` (`src/ProGuard.Core/Data/SchemaUpgrader.cs`)
where it used to call `db.Database.EnsureCreated()`.

## Adding to the model

New entity properties and new `DbSet`s are picked up automatically. Change the model and nothing
else; the next time the engine or UI opens an existing database, `EnsureSchema`:

1. creates the database if it doesn't exist (`EnsureCreated`);
2. creates each entity table that is missing, with the same SQL `EnsureCreated` would use, plus its
   indexes and `HasData` seed rows;
3. adds each column that is missing from an existing table (`ALTER TABLE ... ADD COLUMN`) with the
   SQLite type and nullability EF would give it, and creates missing indexes.

Existing rows get, for each new column, the first of:

- the default configured in the model (`HasDefaultValue` / `HasDefaultValueSql`);
- the value a newly constructed entity has, i.e. the property initializer
  (`public int Foo { get; set; } = 5;` gives existing rows `5`, the same value a brand-new
  database's seed row gets);
- `NULL` for a nullable column, otherwise the CLR default (`0`, `false`, `''`, ...).

So give a new non-nullable property an initializer if its CLR default isn't the right value for
existing rows.

It runs once per database file per process (later calls return immediately), takes no write lock
when the schema is already current, and applies changes inside a `BEGIN IMMEDIATE` transaction after
re-reading the schema, so the engine and UI can both call it at startup.

## Not supported

`EnsureSchema` only ever adds. Anything else needs hand-written upgrade code:

- **Renaming** a property, column or table: it looks like a new column/table; the old one stays
  behind with its data, and the new one starts out at its default.
- **Removing** a property or entity: the column/table stays in the database, unused. Harmless,
  unless it is `NOT NULL` without a default, in which case inserts will fail.
- **Changing a column's type, nullability or default**, or the primary key: existing columns are
  left as they are.
- **Foreign keys on added columns**: SQLite can't add a constraint to an existing table, so the
  column is added without it. (New tables get their foreign keys.)
- **Unique indexes on existing data**: if existing rows violate a new unique index it is skipped.
- **Seed data for existing tables**: `HasData` rows are only inserted into tables created by the upgrade.
