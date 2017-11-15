﻿using System;

namespace D
{
    public class Parameter
    {
        public static readonly Parameter Object  = Get(Kind.Object);
        public static readonly Parameter String  = Get(Kind.String);
        public static readonly Parameter Byte    = Get(Kind.Byte);
        public static readonly Parameter Number  = Get(Kind.Number);
        public static readonly Parameter Decimal = Get(Kind.Decimal);
        public static readonly Parameter Int64   = Get(Kind.Int64);

        public Parameter(string name)
        {
            Name = name;
            Type = new Type(Kind.Object);
        }

        public Parameter(string name, Kind kind)
        {
            Name = name;
            Type = new Type(kind);
            Direction = ParameterDirection.In;
        }

        public Parameter(string name, 
            Type type, 
            bool isOptional = false,
            object defaultValue = null,
            ParameterDirection direction = ParameterDirection.In)
        {
            Name = name;
            Type = type;
            DefaultValue = defaultValue;
            Direction = direction;

            if (isOptional)
            {
                Flags |= ParameterFlags.Optional;
            }
        }

        public Parameter(Type type)
        {
            Type = type ?? throw new ArgumentNullException(nameof(type));
        }
        
        public string Name { get; }

        public Type Type { get; }

        public ParameterFlags Flags { get; }

        public object DefaultValue { get; }
        
        // x: Integer where value > 0 && value < 4

        public Predicate Predicate { get; }

        public ParameterDirection Direction { get; }

        // TODO: cache on kind
        public static Parameter Get(Kind kind) => new Parameter(new Type(kind));

        #region Flags

        public bool IsOptional => Flags.HasFlag(ParameterFlags.Optional);

        public bool IsReadOnly => Flags.HasFlag(ParameterFlags.ReadOnly);

        #endregion
    }

    
    public enum ParameterFlags
    {
        None     = 0,
        Optional = 1 << 0,
        ReadOnly = 1 << 1
    }

    public enum ParameterDirection
    {
        In      = 1,
        Out     = 2,
        InOut   = 3
    }
}