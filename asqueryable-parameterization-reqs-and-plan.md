We are now on V6 and I've made some new tests/observations...

It looks like now you can parameterize with `.SelectQuery` however it can get ugly;

By that, I mean; if you try to chain `.SelectQuery` and `.UnionAll`, you get SQL like:

```
SELECT
    @Id,
    @Data
UNION ALL
SELECT
    @Id_1,
    @Data_1
UNION ALL
SELECT
    @Id_2,
    @Data_2
UNION ALL
```

Which 'works' but is very hard on the eyes and may have other detriments compared to what happens when you do `.AsQueryable(this IEnumerable, IDataContext)` and the values are inlined, e.x.

```
SELECT
    [t1].[Id],
    [t1].[Data]
FROM
    (
        SELECT NULL [Id], NULL [Data] WHERE 1 = 0
        UNION ALL
        VALUES
            (0,'Data 0'), (1,'Data 1'), (2,'Data 2'), (3,'Data 3'), --etc
        ) [t1]
```

Being able to effectively parameterize parts, or even living with 'all' of the row being parameterized, lets us provide specialized code paths where, instead of having to repeatedly call `.InsertWithIdentity` we could call `.InsertWithOutputAsync` (while making sure our output clause returns the right columns to re-match to the other records to insert with next statement. The issue with using the `.AsQueryable` without parameters, is that when the rows are for something like an event sourced system where one column is a large binary value, it would be better to parameterize that binary value rather than bloat the SQL

Biggest win is it would allow users to more easily use `InsertWithOutput/Async` or `UpdateWithOutput/Async` for more complex scenarios without the weirdness/churn of SelectQuery pattern. (That said, I will note that `Sql.Parameter` makes `SelectQuery` a good bit less painful now at least.)

I guess there are a few options that come to mind here, not sure which is better or worse? I put it together like this cause I remembered we had this discussion open and I didn't want to open a feature request/etc without at least getting feedback here (i.e. avoiding churn...)

#### Option 1: Somehow optimize the case of `.SelectQuery=>.UnionAll(.SelectQuery)` so that the output winds up more like the `.AsQueryable` case.

This would at least make the weirdness of actually doing the pattern more on the .NET side and at least SQL side is cleaner

#### Option 2: Add an overload `.AsQueryable(this IEnumerable, IDataContext, bool)` that parameterizes the whole shebang.

I don't like this one as much because it feels too blunt, or at the bare minimum the use of a `bool` is a smell to me.

#### Option 3: Add an overload along the lines of `.AsQueryable<T>(this IEnumerable<T>, IDataContext, Expression<Func<T,T>>)` where the expression is a constructor to the args you want to treat as parameter (or something like that)

Happy to consider a better API option here... but something that accomplishes the same (i.e. being able to specify which columns are parameters) would be the ideal state...

#### Option 4: There's a way to cleanly do this and I just don't realize it.

Again, in this case I'd be willing to update docs accordingly.
