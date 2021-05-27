﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using Kafka.DotNet.ksqlDB.Infrastructure.Extensions;
using Kafka.DotNet.ksqlDB.KSql.Linq;
using Kafka.DotNet.ksqlDB.KSql.Query.Functions;
using Kafka.DotNet.ksqlDB.KSql.Query.Visitors;

namespace Kafka.DotNet.ksqlDB.KSql.Query
{
  internal class KSqlVisitor : ExpressionVisitor
  {
    private readonly StringBuilder stringBuilder;
    private readonly bool useTableAlias;

    internal StringBuilder StringBuilder => stringBuilder;

    public KSqlVisitor()
    {
      stringBuilder = new();
    }

    internal KSqlVisitor(StringBuilder stringBuilder, bool useTableAlias)
    {
      this.stringBuilder = stringBuilder;
      this.useTableAlias = useTableAlias;
    }

    public string BuildKSql()
    {
      var ksql = stringBuilder.ToString();

      stringBuilder.Clear();

      return ksql;
    }

    public string BuildKSql(Expression expression)
    {
      stringBuilder.Clear();

      Visit(expression);

      return stringBuilder.ToString();
    }

    public override Expression? Visit(Expression? expression)
    {
      if (expression == null)
        return null;

      //https://docs.ksqldb.io/en/latest/developer-guide/ksqldb-reference/quick-reference/
      switch (expression.NodeType)
      {
        case ExpressionType.Constant:
          VisitConstant((ConstantExpression)expression);
          break;
          
        //arithmetic
        case ExpressionType.Add:
        case ExpressionType.Subtract: 
        case ExpressionType.Divide: 
        case ExpressionType.Multiply: 
        case ExpressionType.Modulo: 
        //conditionals
        case ExpressionType.AndAlso:
        case ExpressionType.OrElse:
        case ExpressionType.NotEqual:
        case ExpressionType.Equal:
        case ExpressionType.GreaterThan:
        case ExpressionType.GreaterThanOrEqual:
        case ExpressionType.LessThan:
        case ExpressionType.LessThanOrEqual:
        //arrays
        case ExpressionType.ArrayIndex:
          VisitBinary((BinaryExpression)expression);
          break;

        case ExpressionType.Lambda:
        case ExpressionType.TypeAs:
          base.Visit(expression);
          break;

        case ExpressionType.New:
          VisitNew((NewExpression)expression);
          break;

        case ExpressionType.MemberAccess:
          VisitMember((MemberExpression)expression);
          break;

        case ExpressionType.Call:
          VisitMethodCall((MethodCallExpression)expression);
          break;

        case ExpressionType.Convert:
        case ExpressionType.ConvertChecked:
        case ExpressionType.ArrayLength:
        case ExpressionType.Not:
          VisitUnary((UnaryExpression)expression);
          break;

        case ExpressionType.NewArrayInit:
          VisitNewArray((NewArrayExpression)expression);
          break;

        case ExpressionType.ListInit:
          VisitListInit((ListInitExpression)expression);
          break;

        case ExpressionType.MemberInit:
          VisitMemberInit((MemberInitExpression)expression);
          break;
        
        case ExpressionType.Conditional:
          VisitConditional((ConditionalExpression) expression);
          break;
      }

      return expression;
    }

    protected override Expression VisitConditional(ConditionalExpression node)
    {
      Append(" WHEN ");
      Visit(node.Test);
      
      Append(" THEN ");
      Visit(node.IfTrue);
      
      if(node.IfFalse.NodeType != ExpressionType.Conditional)
        Append(" ELSE ");
      
      Visit(node.IfFalse);

      return node;
    }

    protected override Expression VisitMemberInit(MemberInitExpression node)
    {
      Append("STRUCT(");

      bool isFirst = true;
      foreach (var memberBinding in node.Bindings.Where(c => c.BindingType == MemberBindingType.Assignment).OfType<MemberAssignment>())
      {        
        if (isFirst)
          isFirst = false;
        else
          Append(ColumnsSeparator);
        
        Append($"{memberBinding.Member.Name} := ");

        Visit(memberBinding.Expression);
      }

      Append(")");

      return node;
    }

    protected override Expression VisitListInit(ListInitExpression listInitExpression)
    {
      var isDictionary = listInitExpression.Type.IsDictionary();
      
      if (isDictionary)
      {
        //MAP('c' := 2, 'd' := 4)
        Append("MAP(");

        bool isFirst = true;
        
        foreach (var elementInit in listInitExpression.Initializers)
        {
          if (isFirst)
            isFirst = false;
          else
            Append(ColumnsSeparator);

          Visit(elementInit.Arguments[0]);
          
          Append(" := ");
          Visit(elementInit.Arguments[1]);
        }

        Append(")");
      }

      return listInitExpression;
    }

    protected override Expression VisitNewArray(NewArrayExpression node)
    {
      Append("ARRAY[");
      PrintCommaSeparated(node.Expressions);
      Append("]");

      return node;
    }

    protected override Expression VisitMethodCall(MethodCallExpression methodCallExpression)
    {      
      var methodInfo = methodCallExpression.Method;

      if (methodCallExpression.Object != null && methodCallExpression.Object.Type.IsDictionary())
      {
        if (methodCallExpression.Method.Name == "get_Item")
        {
          Visit(methodCallExpression.Object);
          Append("[");
          Visit(methodCallExpression.Arguments[0]);
          Append("]");
        }
      }

      TryCast(methodCallExpression);

      if (methodCallExpression.Object == null
          && methodInfo.DeclaringType.Name == nameof(KSqlFunctionsExtensions))
        new KSqlFunctionVisitor(stringBuilder, useTableAlias).Visit(methodCallExpression);

      if (methodCallExpression.Object != null
          && (methodInfo.DeclaringType.Name == typeof(IAggregations<>).Name || methodInfo.DeclaringType.Name == nameof(IAggregations)))
      {
        new AggregationFunctionVisitor(stringBuilder, useTableAlias).Visit(methodCallExpression);
      }

      if (methodCallExpression.Type == typeof(string))
      {
        if (methodInfo.Name == nameof(string.ToUpper))
        {
          Append("UCASE(");
          Visit(methodCallExpression.Object);
          Append(")");
        }
        if (methodInfo.Name == nameof(string.ToLower))
        {
          Append("LCASE(");
          Visit(methodCallExpression.Object);
          Append(")");
        }
      }

      return methodCallExpression;
    }

    private void TryCast(MethodCallExpression methodCallExpression)
    {
      var methodName = methodCallExpression.Method.Name;

      if (methodName.IsOneOfFollowing(nameof(string.ToString), nameof(Convert.ToInt32), nameof(Convert.ToInt64), nameof(Convert.ToDecimal), nameof(Convert.ToDouble)))
      {
        Append("CAST(");

        Visit(methodCallExpression.Arguments.Count >= 1
          ? methodCallExpression.Arguments[0]
          : methodCallExpression.Object);

        string ksqlType = methodName switch
        {
          nameof(string.ToString) => "VARCHAR",
          nameof(Convert.ToInt32) => "INT",
          nameof(Convert.ToInt64) => "BIGINT",
          nameof(KSQLConvert.ToDecimal) => $"DECIMAL({methodCallExpression.Arguments[1]},{methodCallExpression.Arguments[2]})",
          nameof(Convert.ToDouble) => "DOUBLE",
        };

        Append($" AS {ksqlType})");
      }
    }

    protected void PrintFunctionArguments(IEnumerable<Expression> expressions)
    {
      Append("(");

      PrintCommaSeparated(expressions);

      Append(")");
    }

    protected void PrintCommaSeparated(IEnumerable<Expression> expressions)
    {
      bool isFirst = true;

      foreach (var expression in expressions)
      {
        if (isFirst)
          isFirst = false;
        else
          stringBuilder.Append(ColumnsSeparator);

        Visit(expression);
      }
    }

    protected override Expression VisitConstant(ConstantExpression constantExpression)
    {
      if (constantExpression == null) throw new ArgumentNullException(nameof(constantExpression));

      var value = constantExpression.Value;

      if (value is not string && value is IEnumerable enumerable)
      {
        Append(enumerable);
      }
      else switch (value)
      {
        case ListSortDirection listSortDirection:
        {
          string direction = listSortDirection == ListSortDirection.Ascending ? "ASC" : "DESC";
          stringBuilder.Append($"'{direction}'");
          break;
        }
        case string:
          stringBuilder.Append($"'{value}'");
          break;
        default:
        {
          var stringValue = value != null ? value.ToString() : "NULL";

          stringBuilder.Append(stringValue ?? "Unknown");
          break;
        }
      }

      return constantExpression;
    }

    private const string OperatorAnd = "AND";

    private static readonly ISet<ExpressionType> SupportedBinaryOperators = new HashSet<ExpressionType>
    {
      ExpressionType.Add,
      ExpressionType.Subtract, 
      ExpressionType.Divide, 
      ExpressionType.Multiply, 
      ExpressionType.Modulo, 
      ExpressionType.AndAlso,
      ExpressionType.OrElse,
      ExpressionType.NotEqual,
      ExpressionType.Equal,
      ExpressionType.GreaterThan,
      ExpressionType.GreaterThanOrEqual,
      ExpressionType.LessThan,
      ExpressionType.LessThanOrEqual,
    };

    protected override Expression VisitBinary(BinaryExpression binaryExpression)
    {
      if (binaryExpression == null) throw new ArgumentNullException(nameof(binaryExpression));

      bool IsBinaryOperation(ExpressionType expressionType) => SupportedBinaryOperators.Contains(expressionType);

      bool shouldAddParentheses = IsBinaryOperation(binaryExpression.Left.NodeType);

      if(shouldAddParentheses)
        Append("(");

      Visit(binaryExpression.Left);

      if(shouldAddParentheses)
        Append(")");

      if (binaryExpression.NodeType == ExpressionType.ArrayIndex)
      {
        Append("[");
        Visit(binaryExpression.Right);
        Append("]");

        return binaryExpression;
      }

      //https://docs.ksqldb.io/en/latest/reference/sql/appendix/
      string @operator = binaryExpression.NodeType switch
      {
        //arithmetic
        ExpressionType.Add => "+",
        ExpressionType.Subtract => "-",
        ExpressionType.Divide => "/",
        ExpressionType.Multiply => "*",
        ExpressionType.Modulo => "%",
        //conditionals
        ExpressionType.AndAlso => OperatorAnd,
        ExpressionType.OrElse => "OR",
        ExpressionType.Equal when binaryExpression.Right is ConstantExpression ce && ce.Value == null => "IS",
        ExpressionType.Equal => "=",
        ExpressionType.NotEqual when binaryExpression.Right is ConstantExpression ce && ce.Value == null => "IS NOT",
        ExpressionType.NotEqual => "!=",
        ExpressionType.LessThan => "<",
        ExpressionType.LessThanOrEqual => "<=",
        ExpressionType.GreaterThan => ">",
        ExpressionType.GreaterThanOrEqual => ">=",
      };

      @operator = $" {@operator} ";

      Append(@operator);
      
      shouldAddParentheses = IsBinaryOperation(binaryExpression.Right.NodeType);

      if(shouldAddParentheses)
        Append("(");

      Visit(binaryExpression.Right);

      if(shouldAddParentheses)
        Append(")");

      return binaryExpression;
    }

    protected override Expression VisitNew(NewExpression newExpression)
    {
      if (newExpression == null) throw new ArgumentNullException(nameof(newExpression));

      if (newExpression.Type.IsAnonymousType())
      {
        bool isFirst = true;

        foreach (var memberWithArguments in newExpression.Members.Zip(newExpression.Arguments, (x, y) => new {First = x, Second = y }))
        {
          if (isFirst)
            isFirst = false;
          else
            Append(ColumnsSeparator);

          switch (memberWithArguments.Second.NodeType)
          {
            case ExpressionType.Not:
            case ExpressionType.TypeAs:
            case ExpressionType.ArrayLength:
            case ExpressionType.Constant:
            case ExpressionType.NewArrayInit:
            case ExpressionType.ListInit:
            case ExpressionType.MemberInit:
            case ExpressionType.Call:
              Visit(memberWithArguments.Second);
              Append(" ");
              break;
            case ExpressionType.MemberAccess:
              if (memberWithArguments.Second is MemberExpression {Expression: MemberInitExpression memberInitExpression} && memberInitExpression.Type.IsStruct())
              {
                Visit(memberWithArguments.Second);
                Append(" ");
              }
              break;
            
            case ExpressionType.Conditional:
              Append("CASE");
              Visit(memberWithArguments.Second);
              Append(" END AS ");
              break;
          }

          if(memberWithArguments.Second is BinaryExpression)
          {
            PrintColumnWithAlias(memberWithArguments.First, memberWithArguments.Second);

            continue;
          }

          if (ShouldAppendAlias(memberWithArguments.First, memberWithArguments.Second)) 
            PrintColumnWithAlias(memberWithArguments.First, memberWithArguments.Second);
          else
            ProcessVisitNewMember(memberWithArguments.First, memberWithArguments.Second);
        }
      }

      return newExpression;
    }

    private bool ShouldAppendAlias(MemberInfo memberInfo, Expression expression)
    {
      if (expression is MemberExpression me2 && me2.Member.DeclaringType.IsGenericType && me2.Member.DeclaringType.GetGenericTypeDefinition() == typeof(IKSqlGrouping<,>))
        return false;

      return expression.NodeType == ExpressionType.MemberAccess && expression is MemberExpression me &&
             me.Member.Name != memberInfo.Name ;
    }

    private void PrintColumnWithAlias(MemberInfo memberInfo, Expression expression)
    {
      Visit(expression);
      Append(" AS ");
      Append(memberInfo.Name);
    }

    protected virtual void ProcessVisitNewMember(MemberInfo memberInfo, Expression expression)
    {
      Append(memberInfo.Name);
    }

    protected override Expression VisitMember(MemberExpression memberExpression)
    {
      if (memberExpression == null) throw new ArgumentNullException(nameof(memberExpression));

      if (memberExpression.Expression == null)
      {
        new KSqlWindowBoundsVisitor(StringBuilder).Visit(memberExpression);

        return memberExpression;
      }
      
      var memberName = memberExpression.Member.Name;

      if (memberExpression.Expression.NodeType == ExpressionType.Parameter)
      {
        if (useTableAlias)
        {
          Append(((ParameterExpression) memberExpression.Expression).Name);
          Append(".");
        }

        Append(memberExpression.Member.Name);
      }
      else if (memberExpression.Expression.NodeType == ExpressionType.MemberInit)
      {
        Visit(memberExpression.Expression);
        Append("->");
        Append(memberExpression.Member.Name);
      }
      else if (memberExpression.Expression.NodeType == ExpressionType.MemberAccess)
      {
        if (memberName == nameof(string.Length))
        {
          Append("LEN(");
          Visit(memberExpression.Expression);
          Append(")");
        }
        else
          Append($"{memberExpression.Member.Name.ToUpper()}");
      }
      else
      {
        var outerObj = ExtractFieldValue(memberExpression);

        Visit(Expression.Constant(outerObj));
      }

      return memberExpression;
    }

    internal static object ExtractFieldValue(MemberExpression memberExpression)
    {
      var fieldInfo = (FieldInfo) memberExpression.Member;
      var innerMember = (ConstantExpression) memberExpression.Expression;
      var innerField = innerMember.Value;

      object outerObj = fieldInfo.GetValue(innerField);

      return outerObj;
    }

    protected override Expression VisitUnary(UnaryExpression unaryExpression)
    {
      if (unaryExpression == null) throw new ArgumentNullException(nameof(unaryExpression));

      switch (unaryExpression.NodeType)
      {
        case ExpressionType.ArrayLength:
          Append("ARRAY_LENGTH(");
          base.Visit(unaryExpression.Operand);
          Append(")");
          return unaryExpression;
        case ExpressionType.Not:
          Append("NOT ");

          return base.VisitUnary(unaryExpression);
        case ExpressionType.Convert:
        case ExpressionType.ConvertChecked:
          return base.Visit(unaryExpression.Operand);
        default:
          return base.VisitUnary(unaryExpression);
      }
    }

    protected static Expression StripQuotes(Expression expression)
    {
      while (expression.NodeType == ExpressionType.Quote)
      {
        expression = ((UnaryExpression)expression).Operand;
      }

      return expression;
    }

    public void Append(string value)
    {
      stringBuilder.Append(value);
    }

    public void AppendLine(string value)
    {
      stringBuilder.AppendLine(value);
    }

    private const string ColumnsSeparator = ", ";

    protected void Append(IEnumerable enumerable)
    {
      bool isFirst = true;

      foreach (var constant in enumerable)
      {
        if (isFirst)
          isFirst = false;
        else
          stringBuilder.Append(ColumnsSeparator);

        stringBuilder.Append(constant);
      }
    }
  }
}