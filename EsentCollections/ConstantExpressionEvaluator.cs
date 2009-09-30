// --------------------------------------------------------------------------------------------------------------------
// <copyright file="ConstantExpressionEvaluator.cs" company="Microsoft Corporation">
//   Copyright (c) Microsoft Corporation.
// </copyright>
// <summary>
//   Methods to evaluate an expression which returns a T.
// </summary>
// --------------------------------------------------------------------------------------------------------------------

namespace Microsoft.Isam.Esent.Collections.Generic
{
    using System;
    using System.Linq.Expressions;

    /// <summary>
    /// Methods to evaluate an expression which returns a T.
    /// </summary>
    /// <typeparam name="T">The type returned by the expression.</typeparam>
    internal static class ConstantExpressionEvaluator<T>
    {
        /// <summary>
        /// Determine if the given expression is a constant expression, and
        /// return the value of the expression.
        /// </summary>
        /// <param name="expression">The expression to evaluate.</param>
        /// <param name="value">The value of the expression.</param>
        /// <returns>True if the expression was a constant, false otherwise.</returns>
        public static bool TryGetConstantExpression(Expression expression, out T value)
        {
            if (null == expression)
            {
                throw new ArgumentNullException("expression");
            }

            switch (expression.NodeType)
            {
                case ExpressionType.Constant:
                    var constant = (ConstantExpression) expression;
                    value = (T)constant.Value;
                    return true;
            }

            value = default(T);
            return false;
        }        
    }
}