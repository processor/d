﻿// Based on code by Cyril Jandia http://www.cjandia.com/ 
// LICENCE: https://github.com/ysharplanguage/System.Language/blob/master/LICENSE.md

using System.Collections.Generic;

namespace E.Inference;

public sealed class DefineNode : INode
{
    public DefineNode(VariableNode variable, INode body)
    {
        Variable = variable;
        Body = body;
    }

    public VariableNode Variable { get; } // was spec

    public INode Body { get; }

    public override string ToString() => $"{Variable} = {Body}";

    public IType Infer(Environment env, IReadOnlyList<IType> types)
    {
        var known = new List<IType>(types);
        var type = TypeSystem.NewGeneric();
        var varNode = Variable;
            
        env[varNode.Name] = type;

        known.Add(type);

        TypeSystem.Unify(type, TypeSystem.Infer(env, Body, known));

        /*
        if (type.Value.IsConstructor)
        {
            type.Value.Bind(varNode.Id);
        }
        */

        return env[varNode.Name] = type.Value;
    }
}

// Assign?