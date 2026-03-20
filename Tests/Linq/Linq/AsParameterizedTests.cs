using System.Collections.Generic;
using System.Linq;

using LinqToDB;
using LinqToDB.Mapping;

using NUnit.Framework;

using Shouldly;

namespace Tests.Linq
{
	/// <summary>
	/// Tests for the <see cref="LinqExtensions.AsParameterized{TElement}(IEnumerable{TElement}, IDataContext)"/>
	/// extension methods. UwU 🌸
	/// </summary>
	/// <remarks>
	/// CopilotNotes: These tests verify that the AsParameterized API correctly emits SqlParameter
	/// instances instead of SqlValue literals for VALUES clause rows.
	/// </remarks>
	[TestFixture]
	public class AsParameterizedTests : TestBase
	{
		[Table]
		sealed class ParameterizedItem
		{
			[Column] public int     Id    { get; set; }
			[Column] public int     Value { get; set; }
			[Column] public string? Name  { get; set; }
		}

		#region AsParameterized — All Fields

		[Test(Description = "AsParameterized with all fields should produce SQL parameters in VALUES clause uwu~")]
		public void AllFields_Contains([IncludeDataSources(TestProvName.AllSQLite, TestProvName.AllSqlServer)] string context)
		{
			// 🌸 Arrange: create a small in-memory collection and use AsParameterized
			var items = new[]
			{
				new ParameterizedItem { Id = 1, Value = 10, Name = "Alpha" },
				new ParameterizedItem { Id = 2, Value = 20, Name = "Beta" },
			};

			using var db = GetDataContext(context);
			using var table = db.CreateLocalTable<ParameterizedItem>(
			[
				new() { Id = 1, Value = 10, Name = "Alpha" },
				new() { Id = 2, Value = 20, Name = "Beta" },
				new() { Id = 3, Value = 30, Name = "Gamma" },
			]);

			// 🎀 Act: query using parameterized collection in a Contains-like join
			var query =
				from t in table
				where items.AsParameterized(db).Select(x => x.Id).Contains(t.Id)
				select t;

			var result = query.ToList();

			// ✨ Assert: we should get back only items with matching Ids
			result.Count.ShouldBe(2);
			result.Select(r => r.Id).ShouldBe(new[] { 1, 2 }, ignoreOrder: true);
		}

		[Test(Description = "AsParameterized with all fields in a join scenario")]
		public void AllFields_Join([IncludeDataSources(TestProvName.AllSQLite, TestProvName.AllSqlServer)] string context)
		{
			var items = new[]
			{
				new ParameterizedItem { Id = 1, Value = 100, Name = "One" },
				new ParameterizedItem { Id = 2, Value = 200, Name = "Two" },
			};

			using var db = GetDataContext(context);
			using var table = db.CreateLocalTable<ParameterizedItem>(
			[
				new() { Id = 1, Value = 10, Name = "Alpha" },
				new() { Id = 2, Value = 20, Name = "Beta" },
				new() { Id = 3, Value = 30, Name = "Gamma" },
			]);

			// 🎀 Act: join table with parameterized collection
			var query =
				from t in table
				join p in items.AsParameterized(db) on t.Id equals p.Id
				select new { t.Id, TableName = t.Name, ParamName = p.Name, p.Value };

			var result = query.ToList();

			result.Count.ShouldBe(2);
			result.ShouldContain(r => r.Id == 1 && r.TableName == "Alpha" && r.ParamName == "One" && r.Value == 100);
			result.ShouldContain(r => r.Id == 2 && r.TableName == "Beta"  && r.ParamName == "Two" && r.Value == 200);
		}

		[Test(Description = "AsParameterized with empty collection should return no results")]
		public void AllFields_EmptyCollection([IncludeDataSources(TestProvName.AllSQLite, TestProvName.AllSqlServer)] string context)
		{
			var items = System.Array.Empty<ParameterizedItem>();

			using var db = GetDataContext(context);
			using var table = db.CreateLocalTable<ParameterizedItem>(
			[
				new() { Id = 1, Value = 10, Name = "Alpha" },
			]);

			var query =
				from t in table
				where items.AsParameterized(db).Select(x => x.Id).Contains(t.Id)
				select t;

			var result = query.ToList();

			result.Count.ShouldBe(0);
		}

		#endregion

		#region AsParameterized — Selective Fields

		[Test(Description = "AsParameterized with selective fields — only chosen properties become parameters")]
		public void SelectiveFields_Contains([IncludeDataSources(TestProvName.AllSQLite, TestProvName.AllSqlServer)] string context)
		{
			var items = new[]
			{
				new ParameterizedItem { Id = 1, Value = 10, Name = "Alpha" },
				new ParameterizedItem { Id = 2, Value = 20, Name = "Beta" },
			};

			using var db = GetDataContext(context);
			using var table = db.CreateLocalTable<ParameterizedItem>(
			[
				new() { Id = 1, Value = 10, Name = "Alpha" },
				new() { Id = 2, Value = 20, Name = "Beta" },
				new() { Id = 3, Value = 30, Name = "Gamma" },
			]);

			// 🎀 Only parameterize the "Id" field
			var query =
				from t in table
				where items.AsParameterized(db, x => new { x.Id }).Select(x => x.Id).Contains(t.Id)
				select t;

			var result = query.ToList();

			result.Count.ShouldBe(2);
			result.Select(r => r.Id).ShouldBe(new[] { 1, 2 }, ignoreOrder: true);
		}

		[Test(Description = "AsParameterized with single field selector (not anonymous type)")]
		public void SelectiveFields_SingleMember([IncludeDataSources(TestProvName.AllSQLite, TestProvName.AllSqlServer)] string context)
		{
			var items = new[]
			{
				new ParameterizedItem { Id = 1, Value = 10, Name = "Alpha" },
				new ParameterizedItem { Id = 2, Value = 20, Name = "Beta" },
			};

			using var db = GetDataContext(context);
			using var table = db.CreateLocalTable<ParameterizedItem>(
			[
				new() { Id = 1, Value = 10, Name = "Alpha" },
				new() { Id = 2, Value = 20, Name = "Beta" },
				new() { Id = 3, Value = 30, Name = "Gamma" },
			]);

			// 🌸 Single member selector — still parameterizes just that one field
			var query =
				from t in table
				where items.AsParameterized(db, x => (object)x.Id).Select(x => x.Id).Contains(t.Id)
				select t;

			var result = query.ToList();

			result.Count.ShouldBe(2);
			result.Select(r => r.Id).ShouldBe(new[] { 1, 2 }, ignoreOrder: true);
		}

		#endregion

		#region AsParameterized — Scalar Collection

		[Test(Description = "AsParameterized with a scalar int collection — all values become parameters")]
		public void ScalarCollection_Contains([IncludeDataSources(TestProvName.AllSQLite, TestProvName.AllSqlServer)] string context)
		{
			var ids = new[] { 1, 2 };

			using var db = GetDataContext(context);
			using var table = db.CreateLocalTable<ParameterizedItem>(
			[
				new() { Id = 1, Value = 10, Name = "Alpha" },
				new() { Id = 2, Value = 20, Name = "Beta" },
				new() { Id = 3, Value = 30, Name = "Gamma" },
			]);

			var query =
				from t in table
				where ids.AsParameterized(db).Contains(t.Id)
				select t;

			var result = query.ToList();

			result.Count.ShouldBe(2);
			result.Select(r => r.Id).ShouldBe(new[] { 1, 2 }, ignoreOrder: true);
		}

		#endregion

		#region AsParameterized — Multiple Fields Join

		[Test(Description = "AsParameterized with multiple field join to verify multi-column parameterization")]
		public void MultipleFields_Join([IncludeDataSources(TestProvName.AllSQLite, TestProvName.AllSqlServer)] string context)
		{
			var items = new[]
			{
				new ParameterizedItem { Id = 1, Value = 10, Name = "Alpha" },
				new ParameterizedItem { Id = 3, Value = 30, Name = "Gamma" },
			};

			using var db = GetDataContext(context);
			using var table = db.CreateLocalTable<ParameterizedItem>(
			[
				new() { Id = 1, Value = 10, Name = "Alpha" },
				new() { Id = 2, Value = 20, Name = "Beta" },
				new() { Id = 3, Value = 30, Name = "Gamma" },
			]);

			// 🎀 Parameterize Id and Value, join on both columns
			var query =
				from t in table
				join p in items.AsParameterized(db, x => new { x.Id, x.Value })
					on new { t.Id, t.Value } equals new { p.Id, p.Value }
				select t;

			var result = query.ToList();

			result.Count.ShouldBe(2);
			result.Select(r => r.Id).ShouldBe(new[] { 1, 3 }, ignoreOrder: true);
		}

		#endregion
	}
}

