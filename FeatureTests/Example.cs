using System.Linq.Expressions;

using LinqToDB;
using LinqToDB.Data;

namespace FeatureTests
{
	/// <summary>
	/// You can probably ignore this, but It was something I was trying to do to see if it would help.
	/// </summary>
	public static class Example
	{

		/// <summary>
		/// Builds a parameterized <see cref="IQueryable"/> from a collection of items by constructing
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
			IEnumerable<TTable>                                                                  table) where TTable : class
		{
			// 🌟 Nullable because we build it up row by row — starts empty and grows via UNION ALL~ uwu
			IQueryable<TTable>? query = null;
			foreach (var item in table)
			{
				var props   = item.GetType().GetProperties();
				var exprs   = new List<MemberAssignment>();
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
}
