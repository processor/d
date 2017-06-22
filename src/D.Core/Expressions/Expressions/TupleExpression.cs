﻿using System;

namespace D.Expressions
{
    public class TupleExpression : IExpression
    {
        public TupleExpression(IExpression[] elements)
        {
            Elements = elements ?? throw new ArgumentNullException(nameof(elements));
        }

        public int Size => Elements.Length;

        public IExpression[] Elements { get; }

        public Kind Kind => Kind.TupleExpression;
    }

    // a: 100
    public class NamedElement : IExpression
    {
        public NamedElement(string name, IExpression value)
        {
            Name = name;
            Value = value;
        }

        public string Name { get; }

        // type or constant
        public IExpression Value { get; }

        Kind IObject.Kind => Kind.NamedValue;
    }

    // a: [ ] byte
    public class NamedType : IExpression
    {
        public NamedType(string name, TypeSymbol type)
        {
            Name = name;
            Type = type;
        }

        public string Name { get; }

        public TypeSymbol Type { get; }

        Kind IObject.Kind => Kind.NamedType;

    }

    // 1: i32
    public class TypedValue : IObject
    {
        public TypedValue(IExpression value, IType type)
        {
            Value = value;
            Type = type;
        }

        public IExpression Value { get; }

        // type or constant
        public IType Type { get; }

        Kind IObject.Kind => Kind.TypedValue;
    }
}
 