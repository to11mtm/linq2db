using System;
using System.Collections.Generic;
using System.Linq.Expressions;

using LinqToDB.Internal.Expressions;
using LinqToDB.Internal.Extensions;
using LinqToDB.Internal.Reflection;

namespace LinqToDB.Internal.Linq.Builder
{
	/// <summary>
	/// Builder for the <see cref="LinqExtensions.AsQueryableParameterized{TElement}(IEnumerable{TElement}, IDataContext)"/>
	/// and <see cref="LinqExtensions.AsQueryableParameterized{TElement}(IEnumerable{TElement}, IDataContext, Expression{Func{TElement, object}})"/>
	/// extension methods.
	/// </summary>
	/// <remarks>
	/// CopilotNotes: This builder intercepts AsParameterized calls, extracts the optional fields selector,
	/// and creates an <see cref="EnumerableContext"/> with parameterization metadata wired into the
	/// <see cref="LinqToDB.Internal.SqlQuery.SqlValuesTable"/>. When the VALUES clause is later built,
	/// the value getters will emit <see cref="LinqToDB.Internal.SqlQuery.SqlParameter"/> instead of
	/// <see cref="LinqToDB.Internal.SqlQuery.SqlValue"/> for the selected fields.
	/// </remarks>
	[BuildsMethodCall(nameof(LinqExtensions.AsQueryableParameterized))]
	sealed class AsParameterizedBuilder : MethodCallBuilder
	{
		public static bool CanBuildMethod(MethodCallExpression call)
			=> call.IsSameGenericMethod(Methods.LinqToDB.AsParameterized, Methods.LinqToDB.AsParameterizedFields);

		protected override BuildSequenceResult BuildMethodCall(ExpressionBuilder builder, MethodCallExpression methodCall, BuildInfo buildInfo)
		{
			// Both overloads have: source (arg0), dataContext (arg1), optional fieldsSelector (arg2)
			var sourceExpression = methodCall.Arguments[0];

			var collectionType = typeof(IEnumerable<>).GetGenericType(sourceExpression.Type);
			if (collectionType == null)
				return BuildSequenceResult.Error(methodCall);

			var elementType = collectionType.GetGenericArguments()[0];

			// Extract parameterized field names from the optional selector expression
			HashSet<string>? parameterizedFieldNames;

			if (methodCall.Arguments.Count > 2)
			{
				// Selective parameterization: AsParameterized(source, dc, x => new { x.Id, x.Name })
				var selectorArg = methodCall.Arguments[2];

				// Unwrap the Quote wrapper around the lambda expression
				if (selectorArg is UnaryExpression { NodeType: ExpressionType.Quote } quote)
					selectorArg = quote.Operand;

				if (selectorArg is not LambdaExpression lambda)
					return BuildSequenceResult.Error(methodCall);

				parameterizedFieldNames = ExtractFieldNames(lambda.Body);
				if (parameterizedFieldNames == null)
					return BuildSequenceResult.Error(methodCall);
			}
			else
			{
				// All-fields parameterization: AsParameterized(source, dc)
				// Empty HashSet means "parameterize ALL fields"
				parameterizedFieldNames = new HashSet<string>(StringComparer.Ordinal);
			}

			if (!builder.CanBeEvaluatedOnClient(sourceExpression))
				return BuildSequenceResult.Error(sourceExpression);

			var param = builder.ParametersContext.BuildParameter(
				buildInfo.Parent,
				sourceExpression,
				null,
				buildParameterType: ParametersContext.BuildParameterType.InPredicate);

			if (param == null)
				return BuildSequenceResult.Error(sourceExpression);

			var enumerableContext = new EnumerableContext(
				builder.GetTranslationModifier(),
				builder,
				param,
				buildInfo.SelectQuery,
				elementType,
				parameterizedFieldNames);

			return BuildSequenceResult.FromContext(enumerableContext);
		}

		/// <summary>
		/// Extracts member (property) names from a selector expression body.
		/// Supports <c>x => new { x.Prop1, x.Prop2 }</c> (NewExpression) and
		/// <c>x => x.Prop1</c> (single MemberExpression). UwU 🎀
		/// </summary>
		/// <remarks>
		/// CopilotNotes: Returns null if the expression shape is not recognized — we don't want
		/// to silently ignore a malformed selector.
		/// </remarks>
		static HashSet<string>? ExtractFieldNames(Expression body)
		{
			var result = new HashSet<string>(StringComparer.Ordinal);

			// Unwrap Convert (boxing to object) if present
			body = body.UnwrapConvert();

			switch (body)
			{
				// x => new { x.Prop1, x.Prop2 }
				case NewExpression newExpr:
				{
					if (newExpr.Arguments.Count == 0)
						return null;

					foreach (var arg in newExpr.Arguments)
					{
						var unwrapped = arg.UnwrapConvert();
						if (unwrapped is MemberExpression member)
							result.Add(member.Member.Name);
						else
							return null;
					}
					
					break;
				}

				// x => x.Prop1 (single member)
				case MemberExpression member:
				{
					result.Add(member.Member.Name);
					break;
				}

				// x => new SomeType { Prop1 = x.Prop1, Prop2 = x.Prop2 }
				case MemberInitExpression memberInit:
				{
					foreach (var binding in memberInit.Bindings)
					{
						if (binding is MemberAssignment assignment)
						{
							var unwrapped = assignment.Expression.UnwrapConvert();
							if (unwrapped is MemberExpression member)
								result.Add(member.Member.Name);
							else
								return null;
						}
						else
							return null;
					}
					
					break;
				}

				default:
					return null;
			}

			return result.Count > 0 ? result : null;
		}
	}
}

