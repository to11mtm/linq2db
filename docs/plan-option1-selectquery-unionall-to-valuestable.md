# 📋 Plan: Optimize `SelectQuery().UnionAll()` Chains → `SqlValuesTable`

> **Option 1** from `asqueryable-parameterization-reqs-and-plan.md`
>
> *"Somehow optimize the case of `.SelectQuery().UnionAll(.SelectQuery())` so that the output
> winds up more like the `.AsQueryable` case."*

## 🎯 Goal

Transform the verbose per-row SQL:

```sql
SELECT @Id, @Data
UNION ALL
SELECT @Id_1, @Data_1
UNION ALL
SELECT @Id_2, @Data_2
```

Into the compact `VALUES`-based output (on supported providers):

```sql
SELECT
    [t1].[Id],
    [t1].[Data]
FROM
    (
        VALUES
            (@Id, @Data), (@Id_1, @Data_1), (@Id_2, @Data_2)
    ) [t1] ([Id], [Data])
```

Or the compact `SELECT…UNION ALL` fallback (on providers without VALUES support):

```sql
SELECT
    [t1].[Id],
    [t1].[Data]
FROM
    (
        SELECT @Id AS [Id], @Data AS [Data]
        UNION ALL
        SELECT @Id_1, @Data_1
        UNION ALL
        SELECT @Id_2, @Data_2
    ) [t1]
```

This reuses **all existing rendering infrastructure** — no changes to SQL builders needed.

---

## 🏗 Architecture: Post-Optimization SQL AST Shape

After the existing LINQ builders and optimizer passes run, a chain like
`db.SelectQuery(() => new { Id = 1, Data = "A" }).UnionAll(db.SelectQuery(() => new { Id = 2, Data = "B" }))`
produces this SQL AST (verified by tracing through `SetOperationBuilder` → `SubQueryContext` wrapping
→ `MoveSubQueryUp` unwrapping → tableless-subquery removal):

```
SelectQuery (top level)
  FROM: (empty — tables removed by OptimizeSubQueries)
  SELECT: SqlValue(1) AS Id, SqlValue("A") AS Data
  SetOperators:
    [0] UNION ALL → SelectQuery
      FROM: (empty)
      SELECT: SqlValue(2) AS Id, SqlValue("B") AS Data
```

Key observations:
- **Both queries have `HasNoTables: true`** (the `SelectQueryExtensions` extension property)
- **Column expressions are direct `ISqlExpression` nodes** (`SqlValue`, `SqlParameter`, `SqlCastExpression`, etc.)
  — not wrapped in `SqlColumn` references, because `OptimizeSubQueries` replaces column references
  with underlying expressions when removing tableless subqueries
- The existing `QueryHelper.IsConstant()` method covers all relevant expression types:
  `SqlValue`, `SqlParameter`, `SqlCast`, `SqlBinaryExpression`, `SqlNullabilityExpression`,
  plus pure `SqlFunction`/`SqlExpression` (e.g. `DATEADD(...)`, `CURRENT_TIMESTAMP`)

The new optimization converts this into:

```
SelectQuery (top level)
  FROM: SqlTableSource(SqlValuesTable)
    Rows: [ [SqlValue(1), SqlValue("A")], [SqlValue(2), SqlValue("B")] ]
    Fields: [ SqlField("Id", Int32), SqlField("Data", String) ]
  SELECT: SqlField(Id), SqlField(Data)
  SetOperators: (cleared)
```

`BasicSqlBuilder.BuildSqlValuesTable()` already renders `SqlValuesTable` using
`BuildValues()` (VALUES syntax) or `BuildValuesAsSelectsUnion()` (SELECT…UNION ALL fallback)
based on the per-provider `IsValuesSyntaxSupported` flag. **No builder changes needed.**

---

## ✅ Implementation Checklist

### 1. Add optimization method to `SelectQueryOptimizerVisitor`

**File:** `Source/LinqToDB/Internal/SqlQuery/Visitors/SelectQueryOptimizerVisitor.cs`

- [x] Add `bool OptimizeConstantUnionsToValuesTable(SelectQuery selectQuery)` method
  (place near `OptimizeUnions`, around line 492)

**Guard checks — return `false` if any of:**
- [x] `!selectQuery.HasSetOperators`
- [x] `selectQuery.DoNotRemove` (respect `AsSubQuery` hint)
- [x] `!selectQuery.HasNoTables` (main query still references tables)
- [x] `selectQuery.HasWhere || selectQuery.HasGroupBy || selectQuery.HasOrderBy || selectQuery.Select.HasModifier`
- [x] `Any(q.HasWhere || q.HasGroupBy || q.HasOrderBy || q.Select.HasModifier || q.DoNotRemove)` for any set operator query
- [x] Any column expression in the main query or set operator queries does not pass `QueryHelper.IsConstant()`
- [x] Column counts mismatch between main query and any set operator query
- [x] `selectQuery.Select.Columns.Count == 0` (nothing to convert)

**Build the ValuesTable:**
- [x] Collect rows: first row from `selectQuery.Select.Columns` expressions,
  subsequent rows from each `setOperator.SelectQuery.Select.Columns` expressions
- [x] Build `SqlField[]` from the main query's columns:
  ```csharp
  var alias    = column.Alias ?? $"c{i + 1}";
  var dbType   = QueryHelper.GetDbDataType(column.Expression, _mappingSchema);
  var canBeNull = column.Expression.CanBeNullable(NullabilityContext.GetContext(selectQuery));
  var field    = new SqlField(dbType, alias, canBeNull);
  ```
  > `PhysicalName` defaults to `Name` (verified at `SqlField.cs:104`), which is our `alias`.
  > This is what `BuildValuesAsSelectsUnion` uses for `AS [columnName]` in the first row.
- [x] Create `SqlValuesTable` using existing constructor:
  `new SqlValuesTable(fields, rows)`
  (the `SqlField[] fields, List<List<ISqlExpression>> rows` overload at `SqlValuesTable.cs:56`)

**Rewire the SelectQuery:**
- [x] Clear `selectQuery.SetOperators` (use `.Clear()`)
- [x] Clear `selectQuery.From.Tables` and add new `SqlTableSource(valuesTable, null)`
- [x] Replace each `selectQuery.Select.Columns[i].Expression` with the corresponding `SqlField`
  from the values table
- [x] Return `true`

---

### 2. Integrate into the optimizer loop

**File:** `Source/LinqToDB/Internal/SqlQuery/Visitors/SelectQueryOptimizerVisitor.cs`

- [x] Add the call inside `FinalizeAndValidateInternal` (line ~568), **after** `OptimizeUnions`:
  ```csharp
  bool FinalizeAndValidateInternal(SelectQuery selectQuery)
  {
      var isModified = false;

      if (OptimizeGroupBy(selectQuery))
          isModified = true;

      if (OptimizeUnions(selectQuery))
          isModified = true;

      // NEW: collapse constant UNION ALL chains into SqlValuesTable
      if (OptimizeConstantUnionsToValuesTable(selectQuery))
          isModified = true;

      if (OptimizeDistinct(selectQuery))
          isModified = true;

      if (CorrectColumns(selectQuery))
          isModified = true;

      return isModified;
  }
  ```

**Why after `OptimizeUnions`:** `OptimizeUnions` flattens nested union subquery wrappers.
Our optimization needs the flat structure to see all constant-only queries in `SetOperators`.

**Why inside `FinalizeAndValidateInternal`:** This method runs in the inner `do...while` loop
of `VisitSqlQuery`. If the transformation triggers further optimizations (e.g., the new
FROM table gets optimized), subsequent iterations will pick it up.

---

### 3. Verify `SqlValuesTable` constructor (no code changes expected)

**File:** `Source/LinqToDB/Internal/SqlQuery/SqlValuesTable.cs`

- [x] Verify `SqlValuesTable(SqlField[] fields, List<List<ISqlExpression>> rows)` (line 56):
  - Sets `field.Table = this` for each field ✓
  - Sets `Rows = rows` ✓
  - `BuildRows()` returns `Rows` directly when pre-populated (line 97-98) ✓
- [x] Verify rows can contain `SqlParameter` instances — they can, `Rows` is `List<List<ISqlExpression>>` ✓
- [x] Verify rendering code doesn't mutate `Rows` — `BuildValues()` and `BuildValuesAsSelectsUnion()`
  only read from the list ✓

---

### 4. Add tests

**File:** `Tests/Linq/Linq/SelectQueryOptimizationTests.cs`

All tests use Shouldly assertions per project coding guidelines.

- [x] **4a.** `ConstantUnionAllShouldBecomeValuesTable` — structural test
  ```
  Build query: db.SelectQuery(() => new { Id = 1, Data = "a" })
    .Concat(db.SelectQuery(() => new { Id = 2, Data = "b" }))
    .Concat(db.SelectQuery(() => new { Id = 3, Data = "c" }))
  Assert: GetSelectQuery() has no SetOperators
  Assert: FROM table source is SqlValuesTable
  Assert: SqlValuesTable has 2 fields
  ```

- [x] **4b.** `SingleSelectQueryShouldNotBecomeValuesTable` — no-op for single SelectQuery
  ```
  Build query: db.SelectQuery(() => new { Id = 1, Data = "hello" })
  Assert: no SqlValuesTable in FROM
  ```

- [x] **4c.** `NonConstantUnionAllShouldNotBecomeValuesTable` — no conversion when queries reference tables
  ```
  Build query: db.Child.Concat(db.Child)
  Assert: no SqlValuesTable in FROM
  ```

- [x] **4d.** `ConstantUnionShouldNotBecomeValuesTable` — UNION (not UNION ALL) must not convert
  ```
  Build query: db.SelectQuery(() => new { Id = 1 }).Union(db.SelectQuery(() => new { Id = 2 }))
  Assert: SetOperators still present
  ```

- [x] **4e.** `ConstantUnionAllValuesTableShouldProduceCorrectResults` — end-to-end execution
  ```
  Build 3-row union query with concrete values, call .OrderBy(...).ToArray()
  Assert: 3 rows returned with correct values
  ```

---

## ⚠️ Edge Cases & Design Decisions

### Use `QueryHelper.IsConstant()` not `IsConstantFast()`

`IsConstantFast` covers `SqlValue`, `SqlParameter`, `SqlBinaryExpression`, `SqlNullabilityExpression`.
`IsConstant` additionally covers `SqlCastExpression`, pure `SqlFunction`, and pure `SqlExpression`.

**Decision:** Use `IsConstant()` — it handles type casts and pure server-side functions
(`DATEADD`, `CURRENT_TIMESTAMP`, etc.) that commonly appear in `SelectQuery` columns.
These are safe to put in VALUES rows since `BuildExpression()` renders them correctly.

### `DoNotRemove` respected

If `selectQuery.DoNotRemove = true` (set by `AsSubQuery()` builder), we skip the optimization.
Same for any set operator member query.

### Column alias resolution

`SqlColumn.Alias` getter returns `RawAlias ?? GetAlias(Expression)`. For our constant columns,
`GetAlias(Expression)` returns `null` for `SqlValue`/`SqlParameter` since there's no field backing.
So we rely on `RawAlias` (set during build) or generate `c1`, `c2`, etc.

The `SqlField.PhysicalName` property defaults to `Name` (verified at `SqlField.cs:104`).
`BuildValuesAsSelectsUnion` uses `sourceFields[i].PhysicalName` for column aliases in the
first-row SELECT. So field `Name` = the column alias ensures correct aliasing.

### Parameters stay as parameters

`SqlValuesTable.Rows` holds `ISqlExpression` instances. `SqlParameter` instances pass through
untouched. `BuildExpression()` renders them as `@paramName`. No parameter inlining occurs.

### Provider compatibility — no builder changes

| Provider | `IsValuesSyntaxSupported` | Fallback |
|---|---|---|
| SQL Server 2008+, PostgreSQL, SQLite 3.8.3+, DB2 | `true` | `VALUES (…), (…)` |
| SQL Server 2005, Oracle, MySQL, Firebird, ClickHouse, Access, Sybase, SAP HANA, SqlCe, Informix | `false` | `SELECT…UNION ALL SELECT…` |

`BasicSqlBuilder.BuildSqlValuesTable()` handles the dispatch. SQLite has a special override
that adds an empty SELECT + UNION ALL prefix. All existing and tested.

### Type casting on first row

Provider-specific `IsSqlValuesTableValueTypeRequired()` overrides control whether the first row
needs `CAST(... AS type)`. Already handled by the existing rendering path.

---

## 📁 Files to Modify

| File | Change Type |
|---|---|
| `Source/LinqToDB/Internal/SqlQuery/Visitors/SelectQueryOptimizerVisitor.cs` | Add `OptimizeConstantUnionsToValuesTable` + call in `FinalizeAndValidateInternal` |
| `Source/LinqToDB/Internal/SqlQuery/SqlValuesTable.cs` | Verify only — no changes expected |
| `Tests/Linq/Linq/SelectQueryOptimizationTests.cs` | Add 5 test methods |

> **Note:** No changes to `QueryHelper.cs` needed — `IsConstant()` already exists
> and covers all required expression types.

---

## 🔮 Future Enhancements (Out of Scope)

1. **Partial conversion** — convert a subset of a UNION ALL chain where only some are constant-only.
2. **UNION (distinct)** — detect constant-only UNION chains too (requires column count match enforcement,
   which VALUES already provides).
3. **Option 3 from the forum post** — selective parameterization via new `AsQueryable` overload.

## Tests that should work but don't and should:

```csharp
using System.Diagnostics;
using System.Linq.Expressions;
using LinqToDB;
using LinqToDB.Async;
using LinqToDB.Data;
using LinqToDB.Mapping;
using LinqToDB.Tools;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace Linq2Db.Extensions.Tests;

/// <summary>
/// You can probably ignore this, but It was something I was trying to do to see if it would help.
/// </summary>
public static class Example
{

    /// <summary>
    /// Builds a parameterized <see cref="IQueryable{TTable}"/> from a collection of items by constructing
    /// expression trees that wrap each property value via <see cref="Sql.Parameter{T}"/>, so linq2db
    /// emits proper SQL parameters instead of inlining literal values. Rows are unioned together via
    /// <c>UNION ALL</c>. UwU~
    /// </summary>
    /// <typeparam name="TTable">The table/entity type. Must be a reference type.</typeparam>
    /// <param name="table">The rows to turn into a parameterized queryable~ 🌸</param>
    /// <returns>A <see cref="IQueryable{TTable}"/> that can be used in further linq2db queries.</returns>
    /// <remarks>
    /// CopilotNotes: Property values are captured by wrapping the current item in Expression.Constant,
    /// then accessing the property off it via Expression.PropertyOrField — no compilation, no reflection
    /// for value reads, just a clean expression tree that linq2db can walk directly. 🐾
    /// </remarks>
    public static IQueryable<TTable> BuildParameterizedQueryable<TTable>(this DataConnection dataConnection,
        IEnumerable<TTable> table) where TTable : class
    {
        // 🌟 Nullable because we build it up row by row — starts empty and grows via UNION ALL~ uwu
        IQueryable<TTable>? query = null;
        foreach (var item in table)
        {
            var props = item.GetType().GetProperties();
            var exprs = new List<MemberAssignment>();
            var newExpr = Expression.New(typeof(TTable));

            // 💖 Capture the current item as a constant so we can access its properties
            //    directly in the expression tree via Expression.PropertyOrField~ UwU
            var itemConstant = Expression.Constant(item, typeof(TTable));

            foreach (var propertyInfo in props)
            {
                // 🌸 Access the property off the item constant — no compilation or reflection needed~
                //    linq2db will evaluate this expression tree node to get the actual value. 🐾
                var propertyAccess = Expression.PropertyOrField(itemConstant, propertyInfo.Name);
                var memberInit = Expression.Bind(propertyInfo,
                    Expression.Call(typeof(Sql), "Parameter", [propertyInfo.PropertyType], propertyAccess));
                exprs.Add(memberInit);
            }

            var memberInitExpr = Expression.MemberInit(newExpr, exprs);
            query = query == null
                ? dataConnection.SelectQuery(Expression.Lambda<Func<TTable>>(memberInitExpr))
                : query.UnionAll(
                    dataConnection.SelectQuery(Expression.Lambda<Func<TTable>>(memberInitExpr)));
        }

        return query ??
               throw new InvalidOperationException(
                   "🚨 The table collection was empty, senpai~ No rows to build a queryable from! UwU");
    }


}

public class UnitTest1
{
    private SqliteConnectionStringBuilder _csb;
    private readonly SqliteConnection _connection;
    private readonly Func<DataConnection> _factory;
    private readonly List<TestData> _testData;

    public UnitTest1(ITestOutputHelper outputHelper)
    {
        _csb= new SqliteConnectionStringBuilder()
        {
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Shared,
            DataSource = "foo"
        };
        _connection = new SqliteConnection(_csb.ToString());
        _connection.Open();

        // 🌸 Configure DataOptions with tracing enabled so all SQL is piped to test output~ UwU
        // 💖 UseTracing hooks directly into the DataOptions pipeline — every DataConnection created
        //    with these options will emit SQL, parameters, and execution info to the callback. 🐾
        var options = new DataOptions()
            .UseProvider(ProviderName.SQLiteMS)
            .UseConnectionString(_csb.ToString())
            .UseTracing(TraceLevel.Info, info =>
            {
                if (info.SqlText is not null)
                    outputHelper.WriteLine(info.SqlText);
            });

        _factory = () => new DataConnection(options);
        
        using var dataConnection = _factory();

        _testData = Enumerable.Range(0, 2)
            .Select(i => new TestData { Id = i, Data = $"Data {i}" }).ToList();
    }

    [Fact]
    public async Task Using_My_Generic_thing_For_Aggregating_parameterized_SelectQueries()
    {
        using var dataConnection = _factory();
        var res = await dataConnection.BuildParameterizedQueryable(_testData).ToListAsync();
    }

    [Fact]
    public async Task Using_AsQueryable_For_Aggregating_SelectData()
    {
        using var dataConnection = _factory();
        var res = await _testData.AsQueryable(dataConnection).ToListAsync();
    }

    [Fact]
    public async Task Using_Explicit_Aggregate_For_Parameterized_SelectQueries()
    {
        using var dataConnection = _factory();
        var queryable = await _testData.Select(a =>
        {
            return dataConnection.SelectQuery(() => new { a.Id, a.Data });
        }).Aggregate((a, b) => a.Concat(b))
        .ToListAsync();
    }

    // This one won't work because the parameterization is happening at the SelectQuery level, and when we do the Union/Concat, it doesn't know how to handle the fact that the parameters are different for each SelectQuery, so it just bails and inlines them instead. The result is that we get a single row with the last values instead of multiple rows with parameters. This is why we need the optimization to turn it into a VALUES table, so that we can keep the parameters separate and have them all included in the final SQL. Without that optimization, this pattern just doesn't work as intended.
    [Fact]
    public async Task Using_Well_Trying_Explicit_Parameterization_probably_in_A_wrong_Way()
    {
        using var dataConnection = _factory();
        await _testData.AsQueryable(dataConnection).Select(a=>new TestData()
        {
            Id = Sql.Parameter(a.Id),
            Data = Sql.Parameter(a.Data)
        }).ToListAsync();
    }
    
    [Fact]
    public async Task Trying_Explicit_Parameterization_via_InsertWithOutput()
    {
        using var dataConnection = _factory();
        dataConnection.CreateTable<SomeTable>();
        var result = _testData.AsQueryable(dataConnection).Select(a => new TestData()
        {
            Id = Sql.Parameter(a.Id),
            Data = Sql.Parameter(a.Data)
        }).InsertWithOutputAsync(dataConnection.GetTable<SomeTable>(), t => new SomeTable()
        {
            Id = t.Id,
            Data = t.Data
        }).ToBlockingEnumerable().ToList();
    }

    [Fact]
    public async Task Trying_SelectQuery_Concat_via_InsertWithOutput()
    {
        using var dataConnection = _factory();
        dataConnection.CreateTable<SomeTable>();
        var queryable = _testData.Select(a => { return dataConnection.SelectQuery(() => new { a.Id, a.Data }); })
            .Aggregate((a, b) => a.Concat(b))
            .InsertWithOutputAsync(dataConnection.GetTable<SomeTable>(), t => new SomeTable()
            {
                Id = t.Id,
                Data = t.Data
            }).ToBlockingEnumerable().ToList();
    }

}

public class TestData
{
    public int Id { get; set; }
    public string Data { get; set; }
}

public class SomeTable
{
  public int Id {get;set;}
  public string Data {get;set;}
}

```