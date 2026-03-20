using System;
using System.Collections.Generic;
using System.Linq.Expressions;

using LinqToDB.Internal.Expressions;
using LinqToDB.Internal.Extensions;
using LinqToDB.Internal.Reflection;

namespace LinqToDB.Internal.Linq.Builder
{
	[BuildsMethodCall(nameof(LinqExtensions.AsQueryableParameterized))]
	sealed class AsQueryableParameterizedBuilder : MethodCallBuilder
	{
		public static bool CanBuildMethod(MethodCallExpression call)
			=> call.IsSameGenericMethod(Methods.LinqToDB.AsParameterized, Methods.LinqToDB.AsParameterizedFields);

		protected override BuildSequenceResult BuildMethodCall(ExpressionBuilder builder, MethodCallExpression methodCall, BuildInfo buildInfo)
		{
			var sourceExpression = methodCall.Arguments[0];

			var collectionType = typeof(IEnumerable<>).GetGenericType(sourceExpression.Type);
			if (collectionType == null)
				return BuildSequenceResult.Error(methodCall);

			var elementType = collectionType.GetGenericArguments()[0];

			HashSet<string>? parameterizedFieldNames;

			if (methodCall.Arguments.Count > 2)
			{
				var selectorArg = methodCall.Arguments[2];

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

		static HashSet<string>? ExtractFieldNames(Expression body)
		{
			var result = new HashSet<string>(StringComparer.Ordinal);

			body = body.UnwrapConvert();

			switch (body)
			{
				
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

				case MemberExpression member:
				{
					result.Add(member.Member.Name);
					break;
				}

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

