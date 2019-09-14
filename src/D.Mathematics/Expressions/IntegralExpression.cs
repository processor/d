﻿using D.Expressions;

namespace D.Mathematics
{
    // An integral assigns numbers to functions in a way that can describe displacement,
    // area, volume, and other concepts that arise by combining infinitesimal data. I

    public sealed class IntegralExpression : IExpression
    {
        public IntegralExpression(IExpression expression)
        {
            Expression = expression;
        }

        public IExpression Expression { get; }

        public IExpression A { get; }

        public IExpression B { get; }

        public IExpression X { get; }
        
        ObjectType IObject.Kind => ObjectType.Integral;
    }
}