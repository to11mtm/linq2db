﻿using System;

namespace LinqToDB.DataProvider.Firebird
{
	using Extensions;
	using SqlProvider;
	using SqlQuery;

	class FirebirdSqlOptimizer : BasicSqlOptimizer
	{
		public FirebirdSqlOptimizer(SqlProviderFlags sqlProviderFlags) : base(sqlProviderFlags)
		{
		}

		static void SetNonQueryParameter(IQueryElement element)
		{
			if (element.ElementType == QueryElementType.SqlParameter)
			{
				var p = (SqlParameter) element;
				if (p.SystemType == null || p.SystemType.IsScalar(false))
					p.IsQueryParameter = false;
			}
		}

		private bool SearchSelectClause(IQueryElement element)
		{
			if (element.ElementType != QueryElementType.SelectClause) return true;

			new QueryVisitor().VisitParentFirst(element, SetNonQueryParameterInSelectClause);

			return false;
		}

		private bool SetNonQueryParameterInSelectClause(IQueryElement element)
		{
			if (element.ElementType == QueryElementType.SqlParameter)
			{
				var p = (SqlParameter)element;
				if (p.SystemType == null || p.SystemType.IsScalar(false))
					p.IsQueryParameter = false;
				return false;
			}

			if (element.ElementType == QueryElementType.SqlQuery)
			{
				new QueryVisitor().VisitParentFirst(element, SearchSelectClause);
				return false;
			}

			return true;
		}

		public override SelectQuery Finalize(SelectQuery selectQuery)
		{
			CheckAliases(selectQuery, int.MaxValue);

			new QueryVisitor().VisitParentFirst(selectQuery, SearchSelectClause);

			if (selectQuery.QueryType == QueryType.InsertOrUpdate)
			{
				foreach (var key in selectQuery.Insert.Items)
					new QueryVisitor().Visit(key.Expression, SetNonQueryParameter);

				foreach (var key in selectQuery.Update.Items)
					new QueryVisitor().Visit(key.Expression, SetNonQueryParameter);

				foreach (var key in selectQuery.Update.Keys)
					new QueryVisitor().Visit(key.Expression, SetNonQueryParameter);
			}

			selectQuery = base.Finalize(selectQuery);

			switch (selectQuery.QueryType)
			{
				case QueryType.Delete : return GetAlternativeDelete(selectQuery);
				case QueryType.Update : return GetAlternativeUpdate(selectQuery);
				default               : return selectQuery;
			}
		}

		public override ISqlExpression ConvertExpression(ISqlExpression expr)
		{
			expr = base.ConvertExpression(expr);

			if (expr is SqlBinaryExpression)
			{
				SqlBinaryExpression be = (SqlBinaryExpression)expr;

				switch (be.Operation)
				{
					case "%": return new SqlFunction(be.SystemType, "Mod",     be.Expr1, be.Expr2);
					case "&": return new SqlFunction(be.SystemType, "Bin_And", be.Expr1, be.Expr2);
					case "|": return new SqlFunction(be.SystemType, "Bin_Or",  be.Expr1, be.Expr2);
					case "^": return new SqlFunction(be.SystemType, "Bin_Xor", be.Expr1, be.Expr2);
					case "+": return be.SystemType == typeof(string)? new SqlBinaryExpression(be.SystemType, be.Expr1, "||", be.Expr2, be.Precedence): expr;
				}
			}
			else if (expr is SqlFunction)
			{
				SqlFunction func = (SqlFunction)expr;

				switch (func.Name)
				{
					case "Convert" :
						if (func.SystemType.ToUnderlying() == typeof(bool))
						{
							ISqlExpression ex = AlternativeConvertToBoolean(func, 1);
							if (ex != null)
								return ex;
						}

						return new SqlExpression(func.SystemType, "Cast({0} as {1})", Precedence.Primary, FloorBeforeConvert(func), func.Parameters[0]);
				}
			}

			return expr;
		}

	}
}
