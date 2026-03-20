using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;
using LinqToDB;
using LinqToDB.Async;
using LinqToDB.Data;
using LinqToDB.Mapping;
using LinqToDB.Tools;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;
	
#pragma warning disable CA2007
#pragma warning disable MA0076
#pragma warning disable CA1849
namespace FeatureTests
{
	public class UnitTest1
	{
		private          SqliteConnectionStringBuilder _csb;
		private readonly SqliteConnection              _connection;
		private readonly Func<DataConnection>          _factory;
		private readonly List<TestData>                _testData;

		public UnitTest1(ITestOutputHelper outputHelper)
		{
			_csb= new SqliteConnectionStringBuilder()
			{
				Mode       = SqliteOpenMode.Memory,
				Cache      = SqliteCacheMode.Shared,
				DataSource = "foo",
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
			var       res            = await dataConnection.BuildParameterizedQueryable(_testData).ToListAsync();
		}

		[Fact]
		public async Task Using_AsQueryable_For_Aggregating_SelectData()
		{
			using var dataConnection = _factory();
			var       res            = await _testData.AsQueryable(dataConnection).ToListAsync();
		}

		[Fact]
		public async Task Using_AsQueryableParameterized_For_Aggregating()
		{
			using var dataConnection = _factory();
			var       res            = await _testData.AsQueryableParameterized(dataConnection).ToListAsync();
		}

		[Fact]
		public async Task Using_AsQueryableParameterized_For_Aggregating_with_ParamArgs()
		{
			using var dataConnection = _factory();
			var       res            = await _testData.AsQueryableParameterized(dataConnection, a => a.Data).ToListAsync();
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

		[Fact]
		public async Task Using_Well_Trying_Explicit_Parameterization_probably_in_A_wrong_Way()
		{
			using var dataConnection = _factory();
			await _testData.AsQueryable(dataConnection).Select(a=>new TestData()
			{
				Id   = Sql.Parameter(a.Id),
				Data = Sql.Parameter(a.Data),
			}).ToListAsync();
		}
    
		[Fact]
		public async Task Trying_Explicit_Parameterization_via_InsertWithOutput()
		{
			using var dataConnection = _factory();
			dataConnection.CreateTable<SomeTable>();
			var result = _testData.AsQueryable(dataConnection).Select(a => new TestData()
			{
				Id   = Sql.Parameter(a.Id),
				Data = Sql.Parameter(a.Data),
			}).InsertWithOutputAsync(dataConnection.GetTable<SomeTable>(), t => new SomeTable()
			{
				Id   = t.Id,
				Data = t.Data,
			}).ToBlockingEnumerable().ToList();
		}

		[Fact]
		public async Task Trying_SelectQuery_Concat_via_InsertWithOutput()
		{
			using var dataConnection = _factory();
			dataConnection.CreateTable<SomeTable>();
			var queryable = dataConnection.SelectQuery(() => new SomeTable { Id = Sql.Parameter(1), Data = Sql.Parameter("foo") });
			// 🌸 Wrap each property in Sql.Parameter() so linq2db emits proper SQL parameters
			//    instead of trying to bind the whole anonymous type — SQLite doesn't know how
			//    to handle that, senpai~ UwU 💖
			foreach (var item in _testData)
			{
				queryable = queryable.UnionAll(dataConnection.SelectQuery(() => new SomeTable{ Id = Sql.Parameter(item.Id), Data = Sql.Parameter(item.Data) }));
			}

			await queryable.ToListAsync();
			//var queryable = _testData.Select(a => { return dataConnection.SelectQuery(() => new { a.Id, a.Data }); })
			//    .Aggregate((a, b) => a.Concat(b))
			_ = queryable
				.InsertWithOutput(dataConnection.GetTable<SomeTable>(), t => new SomeTable()
				{
					Id   = t.Id,
					Data = t.Data,
				})
				.Select(a=>new{a.Data, a.Id}).ToList();
        
			/*
			Expected SQL (with parameters):
			-- SQLite.MS SQLite
	DECLARE @Id  -- Int32
	SET     @Id = 1
	DECLARE @Data NVarChar(3) -- String
	SET     @Data = 'foo'
	DECLARE @Id_1  -- Int32
	SET     @Id_1 = 0
	DECLARE @Data_1 NVarChar(6) -- String
	SET     @Data_1 = 'Data 0'
	DECLARE @Id_2  -- Int32
	SET     @Id_2 = 1
	DECLARE @Data_2 NVarChar(6) -- String
	SET     @Data_2 = 'Data 1'

	INSERT INTO [SomeTable]
	(
		[Id],
		[Data]
	)
	SELECT
		[t1].[Id],
		[t1].[Id]
	FROM
		(
			SELECT
		[t1].[Id],
		[t1].[Data]
	FROM
		(
			SELECT NULL [Id], NULL [Data] WHERE 1 = 0
			UNION ALL
			VALUES
				(@Id,@Data), (@Id_1,@Data_1), (@Id_2,@Data_2)
			) [t1]
	RETURNING
		[SomeTable].[Id],
		[SomeTable].[Data]

			Actual SQL:
			-- SQLite.MS SQLite
	DECLARE @Id  -- Int32
	SET     @Id = { Id = 1, Data = foo }
	DECLARE @Id_1  -- Int32
	SET     @Id_1 = { Id = 0, Data = Data 0 }
	DECLARE @Id_2  -- Int32
	SET     @Id_2 = { Id = 1, Data = Data 1 }

	INSERT INTO [SomeTable]
	(
		[Id],
		[Data]
	)
	SELECT
		[t1].[Id],
		[t1].[Id]
	FROM
		(
			SELECT NULL [Id] WHERE 1 = 0
			UNION ALL
			VALUES
				(@Id), (@Id_1), (@Id_2)
			) [t1]
	RETURNING
		[SomeTable].[Id],
		[SomeTable].[Data]
			*/
		}
	}

	[SuppressMessage("Design", "MA0048:File name must match type name")]
	public class TestData
	{
		public int    Id   { get; set; }
		public string Data { get; set; } = null!;
	}

	[SuppressMessage("Design", "MA0048:File name must match type name")]
	public class SomeTable
	{
		public int    Id   {get;  set;}
		public string Data { get; set; } = null!;
	}
}
#pragma warning restore CA2007
#pragma warning restore MA0076
#pragma warning restore CA1849
