﻿// An implementation of Hindley Milner's Algorithm W
// Based on code by Cyril Jandia http://www.cjandia.com/ 
// LICENCE: https://github.com/ysharplanguage/System.Language/blob/master/LICENSE.md

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace D.Inference
{
    public static class TypeSystem
    {
        internal abstract class TypeBase : IType
        {
            protected TypeBase(string name)
            {
                Name = name;
            }

            protected TypeBase(string name, IType[] args)
                : this(name)
            {
                Arguments = args ?? Array.Empty<IType>();
            }

            public override string ToString() => Name;

            public virtual string Name { get; }

            public IType Constructor { get; protected set; }

            public IType[] Arguments { get; }

            // AKA Instance
            public IType Self { get; internal set; }

            public IType Value => Self != null ? Self.Value : this;
        }

        internal sealed class TypeParameter : TypeBase
        {
            internal TypeParameter()
                : base(null) { Uid = Interlocked.Increment(ref id); }

            private string alpha;

            private string Alpha()
            {
                int id = Uid;

                // stackalloc char[20]; // ...

                var sb = new StringBuilder();

                while (id-- > 0)
                {
                    var r = id % 26;
                    var c = (char)(r + 97);
                    sb.Insert(0, c);
                    id = (id - r) / 26;
                }

                return sb.ToString();
            }

            private string GetName()
            {
                return Self?.Name ?? string.Concat('`', alpha ?? (alpha = Alpha()));
            }
            
            internal readonly int Uid;

            public override string ToString() => Self?.ToString() ?? base.ToString();

            public override string Name => GetName();
        }

        internal sealed class Type : TypeBase
        {
            internal Type(IType constructor, string name, IType[] args)
                : base(name, args)
            {
                if (name == null) throw new ArgumentNullException(nameof(name));

                Constructor = constructor ?? this;
            }

            public override string ToString()
            {
                if (Arguments.Length == 0) return Name;

                var args = string.Join<IType>(", ", Arguments);

                return string.Format($"{Name}<{args}>");
            }
        }

        private static IType Prune(IType type)
        {
            return type is TypeParameter var && var.Self != null 
                ? (var.Self = Prune(var.Self)) 
                : type;
        }

        private static bool OccursIn(IType t, IType s)
        {
            return ((s = Prune(s)) != t) ? (s is Type ? OccursIn(t, s.Arguments) : false) : true;
        }

        private static bool OccursIn(IType t, IReadOnlyList<IType> types)
        {
            foreach (var type in types)
            {
                if (OccursIn(t, type)) return true;
            }

            return false;
        }

        // Creates a recurssive copy of the type.
        public static IType Fresh(IType t, IReadOnlyList<IType> types)
        {
            return Fresh(t, types, new Dictionary<int, IType>());
        }

        private static IType Fresh(IType t, IReadOnlyList<IType> types, Dictionary<int, IType> variables)
        {
            t = Prune(t);

            if (t is TypeParameter var)
            {
                if (OccursIn(t, types))
                {
                    return t;
                }
                else
                {
                    if (!variables.ContainsKey(var.Uid))
                    {
                        variables[var.Uid] = NewGeneric();
                    }

                    return variables[var.Uid];
                }
            }
            else if (t is Type type)
            {
                return NewType(type.Constructor, type.Name, type.Arguments.Select(arg => Fresh(arg, types, variables)).ToArray());
            }
            else
            {
                throw new Exception($"Exepected Type or GenericType. Was {t.GetType()}");
            }
        }

        private static int id;

        public static readonly IType Function = new Type(null, "Function", null); 

        public static IType NewGeneric() => new TypeParameter();

        public static IType NewType(string id, IType[] args) => new Type(null, id, args);

        public static IType NewType(IType constructor, IType[] args) => new Type(constructor, constructor.Name, args);

        public static IType NewType(IType constructor, string id, IType[] args) => new Type(constructor, id, args);

        public static void Unify(IType t, IType s)
        {
            t = Prune(t);
            s = Prune(s);

            if (t is TypeParameter tGeneric)
            {
                if (t != s)
                {
                    if (OccursIn(t, s))
                    {
                        throw new InvalidOperationException($"Recursive unification of {t} in {s}");
                    }

                    tGeneric.Self = s;
                }
            }
            else if (t is Type && s is TypeParameter)
            {
                Unify(s, t);
            }
            else if (t is Type t_type && s is Type s_type)
            {
                if (t_type.Constructor.Name != s_type.Constructor.Name || t_type.Arguments.Length != s_type.Arguments.Length)
                {
                    throw new InvalidOperationException($"{t_type} is not compatible with {s_type}");
                }

                for (var i = 0; i < t_type.Arguments.Length; i++)
                {
                    Unify(t_type.Arguments[i], s_type.Arguments[i]);
                }
            }
            else
            {
                throw new InvalidOperationException($"undecided unification for {t} and {s}");
            }
        }

        public static IType Infer(this Environment env, Node node)
        {
            return Infer(env, node, Array.Empty<IType>());
        }

        public static IType Infer(this Environment env, Node node, IReadOnlyList<IType> types)
        {
            return node.Infer(env, types);
        }
    }
}