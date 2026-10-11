using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace ProGuard.Core.Data;

/// <summary>
/// Brings an existing SQLite database up to the shape of the current EF model without migrations.
/// <para>
/// <c>EnsureCreated</c> only creates a database that has no tables at all, so a property or entity added in
/// a later version would never reach a database created by an earlier one. <see cref="EnsureSchema"/> also
/// creates any entity table that is missing and adds any column that is missing from an existing table
/// (plus missing indexes). It only ever adds: nothing is dropped, renamed or retyped.
/// </para>
/// </summary>
public static class SchemaUpgrader
{
    // "<context type>|<full db path>" for every database already checked by this process.
    private static readonly ConcurrentDictionary<string, bool> Checked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Creates the database if needed and adds any table, column or index the model has and the database
    /// lacks. Use this instead of <c>Database.EnsureCreated()</c>. Idempotent, and after the first successful
    /// call for a database file it returns without touching the database. Safe to call from several
    /// processes at once: changes are made inside an immediate (write-locked) transaction after re-reading
    /// the schema, so only one caller applies them.
    /// </summary>
    public static void EnsureSchema(this DbContext context)
    {
        var cacheKey = GetCacheKey(context, out var dbPath);
        if (cacheKey != null && Checked.ContainsKey(cacheKey) && File.Exists(dbPath)) return;

        try
        {
            context.Database.EnsureCreated();
        }
        catch (SqliteException ex) when (IsAlreadyExists(ex))
        {
            // Another process created the tables between EnsureCreated's check and its CREATE TABLE.
            // Its transaction was rolled back; whatever is still missing gets created below.
        }

        Upgrade(context);

        if (cacheKey != null) Checked[cacheKey] = true;
    }

    private static string? GetCacheKey(DbContext context, out string? dbPath)
    {
        dbPath = null;
        var connectionString = context.Database.GetConnectionString();
        if (string.IsNullOrEmpty(connectionString)) return null;

        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (string.IsNullOrEmpty(builder.DataSource)
            || builder.DataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase)
            || builder.Mode == SqliteOpenMode.Memory)
            return null;

        dbPath = Path.GetFullPath(builder.DataSource);
        return context.GetType().FullName + "|" + dbPath;
    }

    private static void Upgrade(DbContext context)
    {
        var model = context.GetService<IDesignTimeModel>().Model;
        var differ = context.GetService<IMigrationsModelDiffer>();
        var sqlGenerator = context.GetService<IMigrationsSqlGenerator>();

        // Everything EnsureCreated would run against an empty database: one CreateTable per entity table,
        // then its indexes and seed rows.
        var createOperations = differ.GetDifferences(null, model.GetRelationalModel());

        var connection = (SqliteConnection)context.Database.GetDbConnection();
        context.Database.OpenConnection();
        try
        {
            // Read-only pass first: an up-to-date database (the normal case) never takes a write lock, which
            // also matters for a caller that may only read the database file.
            if (PlanChanges(connection, null, createOperations, model).Count == 0) return;

            // BEGIN IMMEDIATE: takes the write lock up front, waiting (busy timeout) for another process
            // doing the same upgrade. The schema is re-read under the lock, so the loser finds nothing to do.
            using var transaction = connection.BeginTransaction(deferred: false);
            foreach (var operation in PlanChanges(connection, transaction, createOperations, model))
            {
                foreach (var command in sqlGenerator.Generate(new[] { operation }, model))
                {
                    try
                    {
                        Execute(connection, transaction, command.CommandText);
                    }
                    catch (SqliteException ex) when (IsAlreadyExists(ex))
                    {
                        // Should not happen under the write lock, but a column or table that already exists is
                        // exactly the state we wanted.
                    }
                    catch (SqliteException ex) when (operation is CreateIndexOperation { IsUnique: true }
                                                     && ex.SqliteErrorCode == 19 /* SQLITE_CONSTRAINT */)
                    {
                        // Existing rows violate a newly added unique index. Leave the data alone and go
                        // without the index rather than refusing to open the database.
                    }
                }
            }
            transaction.Commit();
        }
        finally
        {
            context.Database.CloseConnection();
        }
    }

    /// <summary>The operations needed to bring the database to the model, in the order they must run.</summary>
    private static List<MigrationOperation> PlanChanges(
        SqliteConnection connection, SqliteTransaction? transaction,
        IReadOnlyList<MigrationOperation> createOperations, IModel model)
    {
        var existingTables = ReadNames(connection, transaction,
            "SELECT name FROM sqlite_master WHERE type = 'table'");
        var existingIndexes = ReadNames(connection, transaction,
            "SELECT name FROM sqlite_master WHERE type = 'index'");

        var plan = new List<MigrationOperation>();
        var newTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var createTable in createOperations.OfType<CreateTableOperation>())
        {
            if (!existingTables.Contains(createTable.Name))
            {
                plan.Add(createTable);
                newTables.Add(createTable.Name);
                continue;
            }

            var existingColumns = ReadNames(connection, transaction,
                $"SELECT name FROM pragma_table_info({QuoteLiteral(createTable.Name)})");
            foreach (var column in createTable.Columns)
            {
                if (existingColumns.Contains(column.Name)) continue;
                column.Table = createTable.Name;
                column.Schema = createTable.Schema;
                SetDefaultForExistingRows(column, model);
                plan.Add(column);
            }
        }

        foreach (var operation in createOperations)
        {
            switch (operation)
            {
                case CreateIndexOperation index when newTables.Contains(index.Table)
                                                     || !existingIndexes.Contains(index.Name):
                    plan.Add(index);
                    break;
                // Seed rows only go into tables created just now; an existing table's rows are the user's.
                case InsertDataOperation insert when newTables.Contains(insert.Table):
                    plan.Add(insert);
                    break;
            }
        }

        return plan;
    }

    /// <summary>
    /// Gives a column being added to a table that already has rows the value those rows should take. In order
    /// of preference: the default configured in the model (HasDefaultValue/HasDefaultValueSql), the value a
    /// freshly constructed entity has (its property initializer, which is also what a brand-new database's
    /// seed rows get), and for a NOT NULL column the CLR default of the stored type. SQLite refuses to add a
    /// NOT NULL column without a default.
    /// </summary>
    private static void SetDefaultForExistingRows(AddColumnOperation column, IModel model)
    {
        if (column.DefaultValue != null || column.DefaultValueSql != null || column.ComputedColumnSql != null) return;

        var property = model.GetRelationalModel()
            .FindTable(column.Table, column.Schema)?
            .FindColumn(column.Name)?
            .PropertyMappings.FirstOrDefault()?.Property;

        var initial = property != null ? GetInitializerValue(property) : null;
        if (initial != null)
        {
            var converter = property!.GetTypeMapping().Converter;
            column.DefaultValue = converter != null ? converter.ConvertToProvider(initial) : initial;
            return;
        }

        if (!column.IsNullable)
            column.DefaultValue = ClrDefault(column.ClrType);
    }

    private static object? GetInitializerValue(IProperty property)
    {
        var clrProperty = property.PropertyInfo;
        var entityType = property.DeclaringType.ClrType;
        if (clrProperty == null || entityType.IsAbstract || entityType.GetConstructor(Type.EmptyTypes) == null)
            return null;

        try
        {
            return clrProperty.GetValue(Activator.CreateInstance(entityType));
        }
        catch
        {
            return null; // a constructor with side effects or that throws: fall back to the CLR default
        }
    }

    private static object ClrDefault(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string)) return string.Empty;
        if (type == typeof(byte[])) return Array.Empty<byte>();
        if (type.IsEnum) return Enum.ToObject(type, 0);
        return Activator.CreateInstance(type)!;
    }

    private static HashSet<string> ReadNames(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string QuoteLiteral(string value) => "'" + value.Replace("'", "''") + "'";

    private static bool IsAlreadyExists(SqliteException ex) =>
        ex.SqliteErrorCode == 1 /* SQLITE_ERROR */
        && (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase));
}
