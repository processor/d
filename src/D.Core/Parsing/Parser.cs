﻿using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;

using E.Protocols;
using E.Symbols;
using E.Syntax;

namespace E.Parsing;

using static OperatorType;
using static TokenKind;

public sealed class Parser
{
    private TokenReader _reader;
    private readonly Node _environment;
    private readonly Stack<Mode> _modes = new();

    public Parser(string text)
       : this(text, new Node()) { }

    public Parser(string text, Node environment)
    {
        _reader = new TokenReader(new Tokenizer(text, environment));
        _environment = environment;
        _modes.Push(Mode.Root);
    }

    public static ISyntaxNode Parse(string text)
    {
        var parser = new Parser(text);

        return parser.Next();
    }

    #region Mode

    internal enum Mode
    {
        Root = 1,
        Statement = 2,
        Parenthesis = 3,
        Type = 4,
        Arguments = 5,
        For = 6,
        Block = 7,
        Implementation = 8,
        InterpolatedString = 9,
        Variant = 10,
        Element = 11
    }
    
    private void EnterMode(Mode mode)
    {
        _modes.Push(mode);
    }

    private void LeaveMode(Mode mode)
    {
        var lastMode = _modes.Pop();

        if (lastMode != mode)
        {
            throw new Exception($"Expected {mode} when leaving {lastMode}");
        }
    }

    private bool InMode(Mode mode)
    {
        return _modes.Peek() == mode;
    }

    #endregion

    public List<ISyntaxNode> ReadAll()
    {
        var list = new List<ISyntaxNode>();

        while (!_reader.IsEof)
        {
            list.Add(ReadExpression()!);
        }
        
        return list;
    }

    public bool TryReadNext([NotNullWhen(true)] out ISyntaxNode? node)
    {
        if (_reader.IsEof)
        {
            node = null;

            return false;
        }
        
        node = ReadExpression()!;

        return true;        
    }

    public ISyntaxNode Next()
    {
        if (_reader.IsEof)
        {
            throw new EndOfStreamException();
        }

        return ReadExpression();
    }

    private ISyntaxNode ReadExpression()
    {
        if (_reader.IsEof)
        {
            throw new EndOfStreamException();
        }

        count++;

        if (count > 500)
        {
            ThrowExceededCallDepth();
        }

        switch (_reader.Current.Kind)
        {
            case Null                   : _reader.Consume(); return NullLiteralSyntax.Instance;
            case True                   : _reader.Consume(); return BooleanLiteralSyntax.True;
            case False                  : _reader.Consume(); return BooleanLiteralSyntax.False;

            case Quote                  : return ReadStringLiteral();               // "string"
            case Apostrophe             : return ReadCharacterLiteral();            // 'c'

            case InterpolatedStringOpen : return ReadInterpolatedString();          // $"{expression}"
            case Match                  : return ReadMatch();                       // match expression with ...

            case Var                    : return ReadVar();                         // var (mutable variable)
            case Let                    : return ReadLet();                         // let (immutable constant)

            case For                    : return ReadFor();                         // for 
            case While                  : return ReadWhile();                       // while
            case If                     : return ReadIf();                          // if
            case Return                 : return ReadReturn();                      // return
            case Yield                  : return ReadYield();                       // yield
            case Emit                   : return ReadEmit();                        // emit

            case Observe                : 
            case On                     : return ReadObserveStatement();            // on | observe
            case From                   : return ReadQuery();                       // from x in Collection

            case BracketOpen            : return ReadArrayInitializer();            // [
            case DotDotDot              : return ReadSpread();                      // ... 

            // case Exclamation         : return ReadUnary(Consume(Exclamation));   // !{expression}

            case TagStart               : return ReadElement();                     // <div ...
            case Using                  : return ReadUsingStatement();
        }

        return TopExpression(); // functionName args, 5px * 5
    }

    #region Queries

    public QueryExpression ReadQuery()
    {
        Consume(From);                          // ! from

        // from point in Points
        // where point.a > 0
        // select { a, b }

        var maybeVariable = ReadExpression();

        bool wasVariable = ConsumeIf(In);

        var collection = wasVariable            // ? in
            ? ReadExpression()
            : maybeVariable; // nope, collection

        var variable = wasVariable ? maybeVariable : null;

        // using index_name
        var _ = ConsumeIf(Using)           // ? using
            ? ReadExpression()
            : null;

        var filter = ConsumeIf(Where)         // ? where
            ? ReadExpression()
            : null;

        EnterMode(Mode.Block);

        var map = ConsumeIf(Select)             // ? select
            ? IsKind(BraceOpen)
                ? ReadObjectInitializer(null)   // ? { record }
                : ReadExpression()              // ? variable
            : null;

        LeaveMode(Mode.Block);

        var orderby = ConsumeIf(Orderby)        // ? orderby
            ? new OrderByStatement(
                member     : ReadExpression(),
                descending : ConsumeIf(Descending) // ? descending
            )
            : null;

        ConsumeIf(Ascending);                   // ? ascending

        long skip = ConsumeIf("skip") ? Consume<long>(Number) : 0; // ? skip (number)
        long take = ConsumeIf("take") ? Consume<long>(Number) : 0; // ? take (number)

        return new QueryExpression(collection, variable, filter, map, orderby, skip, take);
    }

    #endregion

    #region Statements: Using, For, While, If, Return

    public UsingStatement ReadUsingStatement()
    {
        Consume(Using);            // ! using

        var domains = new ListBuilder<Symbol>();

        do
        {
            domains.Add(ReadModuleSymbol());
        }
        while (!IsEof && ConsumeIf(Comma)); // ? , 

        ConsumeIf(Semicolon); // ? ;

        return new UsingStatement(domains.ToArray());
    }

    public LambdaExpressionSyntax ReadLambda()
    {
        Consume(LambdaOperator);            // ! =>

        var expression = IsKind(BraceOpen)  // ? {
            ? ReadBlock()
            : ReadExpression();

        ConsumeIf(Semicolon);               // ? ;

        return new LambdaExpressionSyntax(expression);
    }

    public ReturnStatementSyntax ReadYield()
    {
        Consume(Yield);                     // ! yield

        var expression = ReadExpression();  // ! (expression)

        ConsumeIf(Semicolon);               // ? ;

        return new ReturnStatementSyntax(expression);
    }

    public ReturnStatementSyntax ReadReturn()
    {
        Consume(Return);                    // ! return

        var expression = ReadExpression();  // ! (expression)

        ConsumeIf(Semicolon);               // ? ;

        return new ReturnStatementSyntax(expression);
    }

    public EmitStatementSyntax ReadEmit()
    {
        Consume(Emit);                      // ! emit

        var expression = ReadExpression();  // ! (expression)

        ConsumeIf(Semicolon);               // ? ;

        return new EmitStatementSyntax(expression);
    }

    public IfStatementSyntax ReadIf()
    {
        Consume(If);                            // ! if

        EnterMode(Mode.Statement);

        ISyntaxNode condition = ReadExpression();  // ! (condition)

        var body = ReadBlock();                     // { ... }

        ISyntaxNode? elseBranch = IsKind(Else)      // ? else 
            ? ReadElse()
            : null;

        LeaveMode(Mode.Statement);

        return new IfStatementSyntax(condition, body, elseBranch);
    }

    public ISyntaxNode ReadElse()
    {
        Consume(Else);                      // ! else

        var condition = ConsumeIf(If)       // ? if 
            ? ReadExpression()              // ! (condition)
            : null;

        var body = ReadBlock();             // { ... }

        var elseBranch = IsKind(Else)       // ? else
            ? ReadElse()
            : null;

        return condition is not null
            ? new ElseIfStatementSyntax(condition, body, elseBranch)
            : new ElseStatementSyntax(body);
    }

    public WhileStatementSyntax ReadWhile()
    {
        Consume(While); // ! while

        EnterMode(Mode.Statement);

        var condition = ReadExpression();
        var body      = ReadBlock();

        LeaveMode(Mode.Statement);

        return new WhileStatementSyntax(condition, body);
    }

    // for 1..10 { }  
    // for x { } 
    // for x in y { }

    public ForStatementSyntax ReadFor()
    {
        Consume(For);

        EnterMode(Mode.For);

        ISyntaxNode generatorExpression;
        ISyntaxNode? variableExpression = null;  // variable | pattern

        var first = _reader.Current.Kind is ParenthesisOpen or Underscore or BraceOpen
            ? ReadPattern()
            : ReadExpression();

        if (ConsumeIf(In))                      // ? in
        {
            variableExpression = first;
            generatorExpression = ReadExpression();
        }
        else
        {
            generatorExpression = first;
        }

        LeaveMode(Mode.For);

        return new ForStatementSyntax(variableExpression, generatorExpression, ReadBlock());
    }

    // on instance Event'Type e { }
    private ObserveStatementSyntax ReadObserveStatement()
    {
        _reader.Consume(); // ! on | observe

        var observable  = ReadExpression(); // TODO: handle member access

        var eventType = ReadTypeSymbol();

        var varName = (! (IsKind(BraceOpen) | IsKind(LambdaOperator)))
            ? ReadMethodSymbol()
            : null;

        var body = ReadBody();

        var until = IsKind(Until)
            ? ReadUntilExpression()
            : null;

        return new ObserveStatementSyntax(observable, eventType, varName, body, until);
    }

    private UntilConditionSyntax ReadUntilExpression()
    {
        Consume(Until); // ! until

        var untilObservable = ReadExpression();
        var untilEventType  = ReadTypeSymbol();

        return new UntilConditionSyntax(untilObservable, untilEventType);
    }

    public BlockSyntax ReadBlock()
    {
        Consume(BraceOpen); // ! {

        EnterMode(Mode.Block);

        var statements = new List<ISyntaxNode>();

        while (!IsKind(BraceClose))
        {
            statements.Add(ReadExpression());
        }

        Consume(BraceClose); // ! }

        LeaveMode(Mode.Block);

        return new BlockSyntax(statements);
    }

    public SpreadExpressionSyntax ReadSpread()
    {
        Consume(DotDotDot); // ! ...

        return new SpreadExpressionSyntax(ReadPrimary());
    }

    #endregion

    #region Declarations (Types, Properties, ...)

    // Float : Number @size(32) { }
    // Int32 type @size(32)
    // Point type <T:Number> : Vector3 { }
    // Point struct { } 
    // Point struct (size: 16, align: 1, layout: Explict)
    public TypeDeclarationSyntax ReadTypeDeclaration(Symbol typeName)
    {
        // record, struct, event, role, actor
        var flags = ReadTypeModifiers();
            
        var args = IsKind(ParenthesisOpen) ? ReadArguments() : [];
        
        // <T: Number>
        // <T: Number = Float64>
        var genericParameters = ReadGenericParameters();

        var baseType = ConsumeIf(Colon) // ? :
            ? ReadTypeSymbol()          // baseType
            : null;

        var annotations = IsKind(At) ? ReadAnnotations() : [];
        var properties  = ReadTypeDeclarationBody();

        ConsumeIf(Semicolon); // ? ;

        return new TypeDeclarationSyntax(typeName, genericParameters, baseType, args, annotations, properties, flags: flags);
    }

    public CompoundTypeDeclarationSyntax ReadCompoundTypeDeclaration(Symbol[] names)
    {
        var flags = ReadTypeModifiers();

        var baseTypes = ConsumeIf(Colon) // ? :
            ? ReadTypeSymbol()           // baseType
            : null;

        var members = ReadTypeDeclarationBody();

        ConsumeIf(Semicolon); // ? ;

        return new CompoundTypeDeclarationSyntax(names, flags, baseTypes, members);
    }

    // let x = 5
    // let x: i8 = 5
    // let x = (5: i8)
    // let x = i8 | None = 8
    // let x: i8 > 0 = 100
    // let x = ƒ(x, y) => x + y

    // Multiple
    // let a = 1, b = 2, c: i32 = 50
    // let x, y, z: Number
    // var a: i8
    // var a = 1

    // Destructuring
    // let (a: Integer, b, c) = instance

    // Modifiers
    // let private | public | mutable


    public ISyntaxNode ReadLet() => ReadLetOrVar(Let);

    public ISyntaxNode ReadVar() => ReadLetOrVar(Var);

    public ISyntaxNode ReadLetOrVar(TokenKind kind)
    {
        Consume(kind); // ! let | var

        var modifiers = ReadModifiers(); // mutable

        if (kind is Var)
        {
            modifiers |= ObjectFlags.Mutable;
        }

        if (ConsumeIf(ParenthesisOpen))
        {
            var list = new ListBuilder<AssignmentElementSyntax>();

            do
            {
                var name = ReadTypeSymbol();

                TypeSymbol? type = ConsumeIf(Colon)
                    ? ReadTypeSymbol()
                    : null;

                list.Add(new AssignmentElementSyntax(name, type));

            } while (ConsumeIf(Comma));

            Consume(ParenthesisClose);

            _reader.Consume("="); // ! =

            var right = ReadExpression();

            ConsumeIf(Semicolon); // ? ;

            return new DestructuringAssignmentSyntax(list.ToArray(), right);
        }

        var declaration = ReadVariableDeclaration(modifiers);

        if (IsKind(Comma))
        {
            return FinishReadingVariableDeclarationList(declaration, modifiers);
        }
        else
        {
            ConsumeIf(Semicolon); // ? ;

            return declaration;
        }
    }

    private CompoundPropertyDeclaration FinishReadingVariableDeclarationList(
        PropertyDeclarationSyntax first,
        ObjectFlags modifiers)
    {
        var properties = new List<PropertyDeclarationSyntax>(4) {
            first
        };

        while (ConsumeIf(Comma))
        {
            properties.Add(ReadVariableDeclaration(modifiers));
        }

        bool inferredFromLast = false;

        foreach (var v in properties)
        {
            if (v.Type is null && v.Value is null)
            {
                inferredFromLast = true;
            }
        }

        if (inferredFromLast)
        {
            var l = new List<PropertyDeclarationSyntax>(properties.Count);
            var k = properties[^1].Type;

            foreach (var var in properties)
            {
                l.Add(new PropertyDeclarationSyntax(var.Name, k, null, var.Flags));
            }

            properties.Clear();
            properties.AddRange(l);
        }

        ConsumeIf(Semicolon); // ? ;

        return new CompoundPropertyDeclaration([.. properties]);
    }

    private PropertyDeclarationSyntax ReadVariableDeclaration(ObjectFlags modifiers)
    {
        ConsumeIf(ParenthesisOpen);     // ? (
            
        var name = ReadVariableSymbol(SymbolFlags.Local);

        ConsumeIf(ParenthesisClose);    // ? )

        return ReadVariableDeclaration(name, modifiers);
    }

    private PropertyDeclarationSyntax ReadVariableDeclaration(Symbol name, ObjectFlags modifiers)
    {
        var type = ConsumeIf(Colon)     // ? :
            ? ReadTypeSymbol()
            : null;

        var value = ConsumeIf('=')       // ? =
            ? IsKind(Function) ? ReadFunctionDeclaration(name) : ReadExpression()
            : null;

        return new PropertyDeclarationSyntax(name, type, value, modifiers);
    }

    private FunctionDeclarationSyntax ReadInitializer()
    {
        var flags = ObjectFlags.Initializer;

        Consume(From); // ! from

        return ReadFunctionDeclaration(new TypeSymbol("initializer"), flags);
    }

    // to String =>
    private FunctionDeclarationSyntax ReadConverter()
    {
        var flags = ObjectFlags.Converter;

        Consume(To); // ! to

        var returnType = ReadTypeSymbol();

        var body = ReadBody();

        ConsumeIf(Semicolon); // ? ;

        return new FunctionDeclarationSyntax([], body, returnType, flags);
    }

    // [index: i32] -> T { ..
    private FunctionDeclarationSyntax ReadIndexerDeclaration()
    {
        var flags = ObjectFlags.Indexer;

        Consume(BracketOpen);

        var parameters = ReadParameters();

        Consume(BracketClose);

        var returnType = ConsumeIf(ReturnArrow)
            ? ReadArgumentSymbol()
            : null;

        var body = ReadBody();

        ConsumeIf(Semicolon); // ? ;

        return new FunctionDeclarationSyntax(parameters, body, returnType, flags);
    }

    private FunctionDeclarationSyntax ReadFunctionDeclaration(ObjectFlags flags = ObjectFlags.None)
    {
        var isOperator = IsKind(Op);

        if (isOperator) flags |= ObjectFlags.Operator;

        var name = isOperator ? new MethodSymbol(_reader.Consume()) : ReadMethodSymbol();

        return ReadFunctionDeclaration(name, flags);
    }

    // clamp ƒ <T> (p: Point<T>, min: Point<T>, max: Point<T>) => Point<T> { }
    private FunctionDeclarationSyntax ReadFunctionDeclaration(
        Symbol name,
        ObjectFlags flags = ObjectFlags.None)
    {
        ConsumeIf(Function); // ? ƒ | function

        if (name is not null && char.IsUpper(name.Name[0]))
        {
            flags |= ObjectFlags.Initializer;
        }

        // generic parameters <T: Number> 
        var genericParameters = ReadGenericParameters();

        ParameterSyntax[] parameters;

        if (ConsumeIf(ParenthesisOpen)) // ! (
        {
            parameters = ReadParameters();

            Consume(ParenthesisClose);  // ! )
        }
        else if (IsKind(Identifier))
        {
            var parameterName = ReadMemberSymbol();

            // Note: parameters MUST not begin with an uppercase letter.

            parameters = [ new ParameterSyntax(parameterName) ];
        }
        else
        {
            flags |= ObjectFlags.Property | ObjectFlags.Instance;

            parameters = [];
        }
            
        var returnType = ConsumeIf(ReturnArrow)
            ? ReadTypeSymbol()
            : null;

        ISyntaxNode? body;

        if (IsKind(Semicolon))
        {
            body = null;

            flags |= ObjectFlags.Abstract;
        }
        else
        {
            body = ReadBody();
        }

        ConsumeIf(Semicolon); // ? ;

        return new FunctionDeclarationSyntax(name, genericParameters, parameters, returnType, body, flags);
    }

    private ParameterSyntax[] ReadGenericParameters()
    {
        if (ConsumeIf(TagStart))
        {
            int i = 0;

            var list = new ListBuilder<ParameterSyntax>();

            do
            {
                var genericName = ReadTypeSymbol();

                var genericType = ConsumeIf(Colon)         // ? : {type}
                    ? ReadTypeSymbol()
                    : null;


                TypeSymbol? defaultValue = ConsumeIf('=') ? ReadTypeSymbol() : null;

                list.Add(new ParameterSyntax(genericName, genericType, defaultValue));

                i++;
            }
            while (ConsumeIf(Comma));

            Consume(TagEnd);

            return list.ToArray();
        }
           
        return [];
    }

    private ISyntaxNode ReadBody() => Current.Kind switch
    {
        LambdaOperator => ReadLambda(), // expression bodied?
        BraceOpen      => ReadBlock(),

        _              => throw new UnexpectedTokenException("Expected block or lambda reading lambda", Current),
    };
        
    private FunctionDeclarationSyntax ReadAnonymousFunctionDeclaration(Symbol parameterName)
    {
        // TODO: determine whether it captures any outside variables 

        var lambda = ReadLambda();

        ConsumeIf(Semicolon); // ? ;

        return new FunctionDeclarationSyntax(
            parameters  : [ new ParameterSyntax(parameterName) ],
            body        : lambda, 
            flags       : ObjectFlags.Anonymous
        );
    }

    public ParameterSyntax[] ReadParameters()
    {
        if (IsKind(ParenthesisClose))
        {
            return [];
        }

        var list = new ListBuilder<ParameterSyntax>();

        int i = 0;

        do
        {
            list.Add(ReadParameter(i));

            i++;
        }
        while (_reader.ConsumeIf(Comma));

        return list.ToArray();
    }
        
    // this
    // x: Integer
    // x: Integer > 0
    // x: Integer where x > 0 && x < 10
    // x: Integer = 0
    // x: Integer = 0 where x > 0
    public ParameterSyntax ReadParameter(int index)
    {
        var name = ReadArgumentSymbol(); // name

        var type = ConsumeIf(Colon) // ? : {type}
            ? ReadTypeSymbol()
            : null;
            
        ISyntaxNode? defaultValue = ConsumeIf('=') // ? = {defaultValue}
            ? ReadExpression()
            : null;
            
        ISyntaxNode? condition = null;

        if (ConsumeIf(Where))                       // where value > 0 && value < 10
        {
            condition = ReadExpression();
        }
        else if (IsKind(Op) && Current.Text is not "=")  // > 0
        {
            condition = MaybeBinary(name, 0);
        }

        var annotations = ReadAnnotations();

        return new ParameterSyntax(
            name         : name, 
            type         : type,
            defaultValue : defaultValue,
            condition    : condition,
            index        : index,
            annotations  : annotations
        );
    }

    // event (?record) | record
    // struct
    // class

    public TypeFlags ReadTypeModifiers()
    {
        var flags = ConsumeIf(Event) ? TypeFlags.Event : TypeFlags.None;

        if (ConsumeIf(Record)) flags |= TypeFlags.Record;
        if (ConsumeIf(Struct)) flags |= TypeFlags.Struct;
        if (ConsumeIf(Class))  flags |= TypeFlags.Class;
        if (ConsumeIf(Role))   flags |= TypeFlags.Role;
        if (ConsumeIf(Actor))  flags |= TypeFlags.Actor;

        return flags;
    }

    // Pascal unit (symbol: "Pa",  value : 1)          : Pressure
    // Radian unit (symbol: "rad", value : 1)          : Angle
    // Degree unit (symbol: "deg", value: (π/180) rad) : Angle

    public UnitDeclarationSyntax ReadUnitDeclaration(Symbol name)
    {
        ConsumeIf(Unit);

        var args = IsKind(ParenthesisOpen) ? ReadArguments() : [];

        var baseType = ConsumeIf(Colon) // ? :
            ? ReadTypeSymbol()          // baseType
            : null;

        ConsumeIf(Semicolon);
            
        return new UnitDeclarationSyntax(name, baseType, args);
    }

    // TODO

    // _ "+" _ operator { precedence: 1, associativity: left }
    public OperatorDeclarationSyntax ReadOperatorDeclaration(Symbol name)
    {
        ConsumeIf(Operator);

        var properties = ReadUnitDeclarationProperties();

        return new OperatorDeclarationSyntax(name, properties);
    }

    public ArgumentSyntax[] ReadUnitDeclarationProperties()
    {
        Consume(BraceOpen);

        var properties = new ListBuilder<ArgumentSyntax>();

        while (!IsKind(BraceClose))
        {
            properties.Add(ReadUnitDeclarationProperty());

            ConsumeIf(Semicolon);
        }

        Consume(BraceClose);

        return properties.ToArray();
    }

    public ArgumentSyntax ReadUnitDeclarationProperty()
    {
        var name = Symbol.Label(ReadName());

        _reader.Read(Colon);

        var value = ReadExpression();

        return new ArgumentSyntax(name, value);

    }

    private IReadOnlyList<ISyntaxNode> ReadTypeDeclarationBody()
    {
        if (ConsumeIf(BraceOpen)) // ! {
        {
            var members = new List<ISyntaxNode>();

            EnterMode(Mode.Block);

            while (!IsKind(BraceClose))
            {
                members.Add(ReadTypeMember());
            }

            LeaveMode(Mode.Block);

            Consume(BraceClose); // ! }

            return members;
        }

        return Array.Empty<ISyntaxNode>();
    }

    private AnnotationSyntax[] ReadAnnotations()
    {
        // @key
        // @member("hello")

        var builder = new ListBuilder<AnnotationSyntax>();

        while (ConsumeIf(At))
        {
            var name = ReadTypeSymbol(); // !{name}

            var args = IsKind(ParenthesisOpen) ? ReadArguments() : [];

            builder.Add(new AnnotationSyntax(name, args));
        }

        return builder.ToArray();
    }

    // Account protocol { }
    // Point protocol : Vector3 { } 

    public ProtocolDeclarationSyntax ReadProtocol(Symbol name)
    {
        Consume(Protocol);      // ! protocol

        var methods = new ListBuilder<FunctionDeclarationSyntax>();

        Symbol? baseProtocol = ConsumeIf(Colon)
            ? ReadTypeSymbol()
            : null;

        var annotations = ReadAnnotations();

        Consume(BraceOpen);   // ! {

        IProtocolMessage[] channelProtocol;

        if (!IsKind(BraceClose))
        {
            channelProtocol = _reader.Current.Equals("*")
                ? ReadProtocolChannel()
                : [];

            while (!IsKind(BraceClose))
            {
                methods.Add(ReadProtocolMember());
            }
        }
        else
        {
            channelProtocol = [];
        }
            
        Consume(BraceClose);  // ! }

        ConsumeIf(Semicolon); // ? ;

        return new ProtocolDeclarationSyntax(name, channelProtocol, methods.ToArray());
    }

    public IProtocolMessage[] ReadProtocolChannel()
    {
        var messages = new ListBuilder<IProtocolMessage>();
        var options  = new ListBuilder<ProtocolMessage>();

        while (ConsumeIf('*'))  // ! ∙
        {
            ConsumeIf(Bar);     // ? |  // Optional leading bar in a oneof set

            var message = ReadProtocolMessage();

            if (message.Fallthrough)
            {
                var flags = ProtocolMessageFlags.None;

                options.Add(message);

                while (message.Fallthrough && !IsKind(Repeats) && _reader.Current.Text is not "*")
                {
                    options.Add(ReadProtocolMessage());
                }

                if (ConsumeIf(Repeats))
                {
                    flags |= ProtocolMessageFlags.Repeats;

                    if (ConsumeIf(Colon))
                    {
                        var label = ReadLabelSymbol(); // the state
                    }
                }

                if (ConsumeIf(Tombstone)) // ? ∎
                {
                    flags |= ProtocolMessageFlags.End;
                }

                var oneOf = new ProtocolMessageChoice(options.ToArray(), flags);

                messages.Add(oneOf);
            }
            else
            {
                messages.Add(message);
            }
        }

        return messages.ToArray();
    }

    //  settle  'Transaction  ƒ (Transaction) -> Transaction' Settlement

    public FunctionDeclarationSyntax ReadProtocolMember()
    {
        var name = ReadLabelSymbol();
            
        ConsumeIf(Function);                          // ? ƒ

        var flags = ObjectFlags.Abstract;

        ParameterSyntax[] parameters;

        if (ConsumeIf(ParenthesisOpen))               // ! (
        {
            parameters = ReadParameters();

            Consume(ParenthesisClose);                // ! )
        }
        else
        {
            flags |= ObjectFlags.Property;

            parameters = []; 
        }

        var returnType = ConsumeIf(ReturnArrow)
            ? ReadTypeSymbol()
            : TypeSymbol.Void;

        ConsumeIf(Semicolon);

        return new FunctionDeclarationSyntax(name, [], parameters, returnType, null, flags);
    }

    // dissolve ∎ : dissolved
    // settling 'Transaction  |
    public ProtocolMessage ReadProtocolMessage()
    {
        var flags = ConsumeIf(Question)   // ? ?
            ? ProtocolMessageFlags.Optional 
            : ProtocolMessageFlags.None;

        var name = ReadMethodSymbol();

        if (ConsumeIf(Tombstone)) // ? ∎
        {
            flags |= ProtocolMessageFlags.End;
        }

        if (ConsumeIf(Bar)) // ? |
        {
            flags |= ProtocolMessageFlags.Fallthrough;
        }

        var label = ConsumeIf(Colon)    // ? :
            ? ReadLabelSymbol().Name
            : null;

        return new ProtocolMessage(name.Name, label, flags);
    }

    #endregion

    #region Modules

    public ModuleSyntax ReadModule(Symbol name)
    {
        Consume(Module);  // ! module  

        var block = ReadBlock();

        return new ModuleSyntax(name, block.Statements);
    }

    #endregion

    #region Class / Implementation

    // Curve implementation for BezierCurve {

    public ImplementationDeclarationSyntax ReadImplementation(Symbol name)
    {
        var members = new ListBuilder<ISyntaxNode>();

        Consume(Implementation); // ! implementation  

        Symbol? protocol = null;
        Symbol type;

        if (ConsumeIf(For)) // ? for
        {
            protocol = name;
            type     = ReadTypeSymbol();
        }
        else
        {
            type = name;
        }

        Consume(BraceOpen); // ! {

        EnterMode(Mode.Block);

        while (!IsKind(BraceClose))
        {
            members.Add(ReadTypeMember());
        }

        LeaveMode(Mode.Block);

        Consume(BraceClose); // ! }

        return new ImplementationDeclarationSyntax(protocol, type, members.ToArray());
    }

    private ISyntaxNode ReadTypeMember()
    {
        // let private v = 1
        // clone function() => Vector3(x, y, z) 

        switch (Current.Kind)
        {
            case Let: return ReadLet();
            case Var: return ReadVar();
        }
            
        var modifiers = ReadModifiers();

        switch (Current.Kind)
        {
            case BracketOpen : return ReadIndexerDeclaration();    // [name: String] -> Point { 
            case To          : return ReadConverter();             // to Type
            case From        : return ReadInitializer();           // from pattern                                

            case Op          :
                modifiers |= ObjectFlags.Instance;

                return ReadFunctionDeclaration(flags: modifiers);  // function |  * | + | ..
            case Identifier  :
                modifiers |= ObjectFlags.Instance;

                var name = ReadMemberSymbol();

                if (IsKind(Comma)) // a, b : Type
                {
                    return FinishReadingVariableDeclarationList(new PropertyDeclarationSyntax(name, null), modifiers);
                }
                     
                return IsKind(Colon)
                    ? ReadVariableDeclaration(name, modifiers)         // {name}: {type}
                    : ReadFunctionDeclaration(name, flags: modifiers); // function |  * | + | ..
        }

        throw new UnexpectedTokenException("Unexpected token reading member", Current);
    }

  
    private ObjectFlags ReadModifiers()
    {
        var flags = ObjectFlags.None;

        while (true)
        {
            switch (Current.Kind)
            {
                case Mutable:
                    _reader.Advance();

                    flags |= ObjectFlags.Mutable;
                    break;

                case Mutating:
                    _reader.Advance();

                    flags |= ObjectFlags.Mutating;
                    break;

                case Public:
                    _reader.Advance();

                    flags |= ObjectFlags.Public;

                    continue;
                case Private:
                    _reader.Advance();

                    flags |= ObjectFlags.Private;

                    continue;

                case Internal:
                    _reader.Advance();

                    flags |= ObjectFlags.Internal;

                    continue;
            }

            return flags;
        }
    }

    #endregion

    #region Symbols

    /*
    A, B type   { }
    A type      { }
    A type : B  { }
    */
    public Symbol ReadDollarSymbol()
    {
        _reader.Consume(Dollar); // read $

        var number = _reader.Consume(Number);

        return new VariableSymbol("$" + number.Text);
    }

    public LabelSymbol ReadLabelSymbol()
    {
        return new LabelSymbol(ReadName());
    }

    // i
    public VariableSymbol ReadVariableSymbol(SymbolFlags flags)
    {
        return new VariableSymbol(ReadName(), flags);
    }

    // MethodName
    // Module::MethodName
    public MethodSymbol ReadMethodSymbol()
    {
        ModuleSymbol? module = null;

        var name = ReadName(); // Identifier | This | Operator

        // :: Module
        while (ConsumeIf(ColonColon))
        {
            module = new ModuleSymbol(name, module);

            name = _reader.Consume(Identifier);
        }

        return new MethodSymbol(module, name);
    }

    public ArgumentSymbol ReadArgumentSymbol()
    {
        return new ArgumentSymbol(ReadName());
    }

    public PropertySymbol ReadMemberSymbol()
    {
        return new PropertySymbol(ReadName());
    }

    public ModuleSymbol ReadModuleSymbol()
    {
        return new ModuleSymbol(ReadName());
    }

    // chains the name if it finds a backtick
    // settle `Transaction
    [SkipLocalsInit]
    private Token ReadName()
    {
        var name = _reader.Consume();

        if (IsKind(Backtick))
        {
            var sb = new ValueStringBuilder(stackalloc char[32]);

            sb.Append(name);

            while (ConsumeIf(Backtick))
            {
                name = _reader.Consume();

                sb.Append(name);
            }

            name = new Token(name.Kind, name.Start, sb.ToString(), name.Trailing);
        }

        return name;
    }

    /*
        Point
    * Point
    [ Point ]
    [ Point<T> ]
    [ geometry::Point<Number> ]
        (A, B) -> C                     | Function<A, B, C>
        A | B                           | Variant<A, B, C>
        A & B                           | Intersection<A, B>
        A?                              | Optional<A>
    */

    private TypeSymbol ReadTypeSymbol()
    {
        if (ConsumeIf(ParenthesisOpen))
        {
            var args = new ListBuilder<Symbol>();

            do
            {
                args.Add(ReadTypeSymbol());
            }
            while (ConsumeIf(Comma));

            Consume(ParenthesisClose);

            bool wasFunction;

            if ((wasFunction = ConsumeIf(ReturnArrow)))
            {
                args.Add(ReadTypeSymbol());
            }

            return new TypeSymbol(wasFunction ? "Function" : "Tuple", args.ToArray());
        }
            
        if ((ConsumeIf(BracketOpen))) // [
        {
            var type = ReadTypeSymbol();
     
            Consume(BracketClose); // ]

            return new TypeSymbol("Array", arguments: [ type ]);
        }

        // Async?

        if (ConsumeIf('*'))
        {
            return new TypeSymbol("Channel", arguments: [ ReadTypeSymbol() ]);
        }

        ModuleSymbol? module = null;

        Token name = ReadName(); // Identifier | This | Operator

        // :: Module
        while (ConsumeIf(ColonColon))
        {
            module = new ModuleSymbol(name, module);

            name = _reader.Consume(Identifier);
        }

        ParameterSymbol[] parameters;

        // <generic parameter list>

        if (Current.Kind is TagStart) // <A:Number, B:Number=Int>
        {
            var genericParameters = ReadGenericParameters();

            parameters = new ParameterSymbol[genericParameters.Length];

            for (int i = 0; i < parameters.Length; i++)
            {
                ParameterSyntax parameterSyntax = genericParameters[i];

                parameters[i] = new ParameterSymbol(parameterSyntax.Name, parameterSyntax.Type);
            }
        }
        else
        {
            parameters = [];
        }

        var result = new TypeSymbol(module, name, parameters);

        // Variant      : A | B 
        // Intersection : A & B

        if (IsKind(Bar) && !InMode(Mode.Variant))
        {
            EnterMode(Mode.Variant);

            var list = new ListBuilder<Symbol>();
            
            list.Add(result);

            while (ConsumeIf(Bar))
            {
                list.Add(ReadTypeSymbol());
            }

            LeaveMode(Mode.Variant);

            return new TypeSymbol("Variant", list.ToArray());
        }
        else if (_reader.Current.Equals("&"))
        {
            var list = new ListBuilder<Symbol>();

            list.Add(result);

            while (ConsumeIf('&'))
            {
                list.Add(ReadTypeSymbol());
            }

            return new TypeSymbol("Intersection", list.ToArray());
        }

        else if (IsKind(ReturnArrow))
        {
            var list = new ListBuilder<Symbol>();

            list.Add(result);

            Consume(ReturnArrow);

            list.Add(ReadTypeSymbol());

            return new TypeSymbol("Function", list.ToArray());
        }


        // Optional ?
        if (name.Trailing is null && ConsumeIf('?')) // ? 
        {
            return new TypeSymbol("Optional", arguments: [ result ]);
        }

        return result;
    }

    #endregion

    #region Initializers

    // (a: 1, b: 2)
    // (a, b)
    public ObjectInitializerSyntax ReadObjectInitializer(TypeSymbol? type)
    {            
        return new ObjectInitializerSyntax(type, ReadArguments());
    }

    // []
    public ISyntaxNode ReadArrayInitializer()
    {
        Consume(BracketOpen); // [

        // Maybe symbol?
        if (ConsumeIf(BracketClose))
        {
            return Symbol.Type("Array", ReadTypeSymbol()); // Array of T
        }

        var rows = 0;
        var stride = 0;

        var elements = new ListBuilder<ISyntaxNode>();

        var elementKind = SyntaxKind.Object;
        var uniform = true;

        while (!IsKind(BracketClose))
        {
            var elementSyntax = ReadPrimary();

            #region Check for uniformity

            if (uniform && elementSyntax is ArrayInitializerSyntax nestedArray)
            {
                if (rows == 0)
                {
                    elementKind = nestedArray.Elements[0].Kind;
                    stride = nestedArray.Elements.Length;
                }

                if (nestedArray.Elements.Length != stride)
                {
                    uniform = false; // jagged
                }
                else
                {
                    foreach (var a in nestedArray.Elements)
                    {
                        if (a.Kind != elementKind)
                        {
                            uniform = false;

                            break;
                        }
                    }
                }

                rows++;
            }

            #endregion

            elements.Add(elementSyntax);

            if (!ConsumeIf(Comma)) break;
        }
        // Note: Allows trailing comma

        Consume(BracketClose); // ! ]

        // [5] Type -> new List(capacity: 5)

        /*
        if (elements.Count == 1 && elements[0].Kind == Kind.NumberLiteral && IsKind(Identifier) && char.IsUpper(reader.Current.Text[0]))
        {
            var name = Symbol.Type("Array", ReadSymbol(SymbolKind.Type));

            return new CallExpressionSyntax(null, name, [ new ArgumentSyntax(elements[0]) ]);
        }
        */

        int? s = null;

        if (uniform && rows > 0)
        {
            s = stride;
        }

        TypeSymbol? elementType = null;

        if (_reader.Current.Kind is Identifier)
        {
            elementType = ReadTypeSymbol();
        }

        return new ArrayInitializerSyntax(elements.ToArray(), stride: s) { ElementType = elementType };
    }


    #endregion

    #region Literals

    // 1
    // 1_000
    // 1e100
    // 1.1
    // 1.1 Pa

    public ISyntaxNode ReadNumber()
    {
        // precision & scale...

        // _ support

        var line = Current.Start.Line;

        var literal = _reader.Current;

        _reader.Advance();

        string text = literal.Text.Contains('_') ? literal.Text.Replace("_", "") : literal.Text;

        int eIndex = text.IndexOf('e');

        if (eIndex > 0)
        {
            var a = double.Parse(text.AsSpan(0, eIndex), provider: CultureInfo.InvariantCulture);
            var b = double.Parse(text.AsSpan(eIndex + 1), provider: CultureInfo.InvariantCulture);

            double result = a * Math.Pow(10, b);

            text = result.ToString(CultureInfo.InvariantCulture);
        }

        if (literal.Trailing is null && ConsumeIf('%'))
        {
            return new QuantitySyntax(new NumberLiteralSyntax(text), "%", 1);
        }

        // Read any immediately preceding unit types and exponents on the same line

        if (IsKind(Identifier) && Current.Start.Line == line) 
        {
            var (name, power) = ReadUnitSymbol();

            var num = new NumberLiteralSyntax(text);

            return new QuantitySyntax(num, name, power);
        }
          
        return new NumberLiteralSyntax(text);
    }

    private (string name, int power) ReadUnitSymbol()
    {
        var name = Consume(Identifier);

        int pow = 1;

        if (IsKind(Superscript))
        {
            pow = E.Superscript.Parse(_reader.Consume().Text);
        }

        return (name, pow);
    }

    public InterpolatedStringExpressionSyntax ReadInterpolatedString()
    {
        Consume(InterpolatedStringOpen); // ! $"

        EnterMode(Mode.InterpolatedString);

        var children = new ListBuilder<ISyntaxNode>();

        while (!IsKind(Quote))
        {
            var expression = IsKind(BraceOpen)
                ? ReadInterpolatedExpression()
                : ReadInterpolatedSpan();

            children.Add(expression);
        }

        Consume(Quote); // ! "

        LeaveMode(Mode.InterpolatedString);

        return new InterpolatedStringExpressionSyntax(children.ToArray());
    }

    public ISyntaxNode ReadInterpolatedExpression()
    {
        Consume(BraceOpen); // ! {

        var expression = ReadExpression();

        Consume(BraceClose); // }

        return expression;
    }

    [SkipLocalsInit]
    public StringLiteralSyntax ReadInterpolatedSpan()
    {
        var sb = new ValueStringBuilder(stackalloc char[32]);

        Token token;

        while (!IsEof && !IsOneOf(Quote, BraceOpen))
        {
            token = _reader.Consume();

            sb.Append(token.Text);
            sb.Append(token.Trailing);
        }

        return sb.ToString();
    }

    public StringLiteralSyntax ReadStringLiteral()
    {
        Consume(Quote);  // "

        var text = Consume(String);

        Consume(Quote); // "

        return new StringLiteralSyntax(text);
    }

    public CharacterLiteralSyntax ReadCharacterLiteral()
    {
        Consume(Apostrophe); // '

        var text = Consume(Character);

        Consume(Apostrophe); // '

        return new CharacterLiteralSyntax(text.Text[0]);
    }

    // (a, b, c)
    // (a: Integer, b: String)
    public TupleExpressionSyntax ReadTuple()
    {
        Consume(ParenthesisOpen);       // ! (

        EnterMode(Mode.Parenthesis);

        var result = FinishReadingTuple(ReadTupleElement());

        LeaveMode(Mode.Parenthesis);

        return result;
    }

    private TupleExpressionSyntax FinishReadingTuple(ISyntaxNode first)
    {
        var elements = new ListBuilder<ISyntaxNode>();

        elements.Add(first);

        while (ConsumeIf(Comma)) // ? ,
        {
            if (_reader.Current.Kind is ParenthesisOpen)
            {
                // nested tuple
                elements.Add(ReadTuple());
            }
            else
            {
                elements.Add(ReadTupleElement());
            }
        }

        Consume(ParenthesisClose); // ! )

        return new TupleExpressionSyntax(elements.ToArray());
    }

    // {expression} | {name}:{expression}

    public ISyntaxNode ReadTupleElement()
    {
        var first = ReadPrimary();

        if (ConsumeIf(Colon))
        {
            if (first is Symbol name)
            {
                var value = ReadExpression();

                return new TupleElementSyntax(name, value);
            }
            else
            {
                throw new Exception($"Unexpected tuple element. Was {first}");
            }
        }
          
        return first;
    }

    #endregion

    #region Matching

    public MatchExpressionSyntax ReadMatch()
    {
        Consume(Match);                     // ! match

        EnterMode(Mode.Statement);

        var expression = ReadExpression();  // ! {expression}

        // ConsumeIf(With);                 // ? with

        ConsumeIf(BraceOpen);               // ? {

        var cases = new ListBuilder<MatchCaseSyntax>();

        // pattern => action
        // ...

        while (!IsKind(BraceClose))
        {
            var pattern = ReadPattern();

            ISyntaxNode? when = null;

            if (ConsumeIf(When))            // ? when
            {
                when = ReadExpression();
            }

            var lambda = ReadLambda();

            cases.Add(new MatchCaseSyntax(pattern, when, lambda));

            ConsumeIf(Comma); // ? ,
        }

        ConsumeIf(BraceClose); // ? }

        LeaveMode(Mode.Statement);

        return new MatchExpressionSyntax(expression, cases.ToArray());
    }

    #endregion

    #region Patterns

    // record  : { a, b, c }
    // tuple   : (a, b, c)
    // type    : (alias: Type)
    // variant : A | B 
    // any     : _
    // range   : 0..10

    public ISyntaxNode ReadPattern()
    {
        switch (_reader.Current.Kind)
        {
            case BraceOpen  : return ReadRecordPattern();
            case Underscore : Consume(Underscore); return AnyPatternSyntax.Default;

            case ParenthesisOpen:
                var tuple = ReadTuple();

                if (tuple.Size is 1)
                {
                    var element = (TupleElementSyntax)tuple.Elements[0];

                    return new TypePatternSyntax((Symbol)element.Value!, new VariableSymbol(element.Name));
                }

                return new TuplePatternSyntax(tuple);

            default:
                var value = MaybeTuple();

                if (value is RangeExpressionSyntax range)
                {
                    return new RangePatternSyntax(range.Start, range.End);
                }

                return new ConstantPatternSyntax(value);
        }
    }

    public TypePatternSyntax ReadRecordPattern()
    {
        throw new Exception("Not yet implemented");
    }

    #endregion

    #region Expressions

    private ISyntaxNode TopExpression(int minPrecedence = 0)
    {
        var left = MaybeTuple();

        return MaybeBinary(left, minPrecedence);
    }

    // https://en.wikipedia.org/wiki/Operator-precedence_parser

    // 1 + 5 ** 3 + 8

    // *=

    private ISyntaxNode MaybeBinary(ISyntaxNode left, int minPrecedence)
    {
        // x = a || b && c

        Operator? op;

        while (IsKind(Op) && (op = _environment.Operators[Infix, _reader.Current]).Precedence >= minPrecedence) // ??
        {
            _reader.Consume(Op);

            // *-, +=, ...
            if (ConsumeIf('='))
            {
                var r = new BinaryExpressionSyntax(op, left, rhs: ReadExpression());

                return new BinaryExpressionSyntax(E.Operator.Assign, left, r);
            }

            var o = op;

            var right = MaybeMemberAccess();

            while (IsKind(Op) && (op = _environment.Operators[Infix, _reader.Current]).Precedence >= o.Precedence)
            {
                right = MaybeBinary(right, o.Precedence);
            }

            left = new BinaryExpressionSyntax(o, left, right) {
                IsParenthesized = InMode(Mode.Parenthesis)
            };

            ConsumeIf(Semicolon);
        }

        // HACK: Ternary
        if (IsKind(Question))
        {
            return ReadTernaryExpression(left);
        }
            
        return left;
    }

    public UnaryExpressionSyntax ReadUnary(Operator op)
    {
        // maybe postfix?
            
        var expression = ReadExpression();

        return new UnaryExpressionSyntax(op, expression);
    }

    public TernaryExpressionSyntax ReadTernaryExpression(ISyntaxNode condition)
    {
        Consume(Question); // ! ?

        var left = ReadExpression();

        Consume(Colon); // !:

        var right = ReadExpression();

        return new TernaryExpressionSyntax(condition, left, right);
    }

    #endregion

    #region Elements

    public ElementSyntax ReadElement()
    {
        EnterMode(Mode.Element);

        Consume(TagStart); // ! <

        var (moduleName, elementName) = ReadElementName();

        ArgumentSyntax[]? args = null;

        while (!(Current.Kind is TagEnd or TagSelfClosed))
        {
            // read optional arguments
            if (Current.Kind is ParenthesisOpen)
            {
                args = ReadArguments();
            }
            else
            {
                // attribute?
                Next();
            }
        }
            
        bool isSelfClosed = Consume().Kind is TagSelfClosed;

        ISyntaxNode[]? children = null;

        // read the children
        if (!isSelfClosed)
        {
            if (!IsKind(TagCloseStart))
            {
                var list = new ListBuilder<ISyntaxNode>();

                while (!IsKind(TagCloseStart) && !IsEof)
                {
                    list.Add(ReadElementChild());
                }

                children = list.ToArray();
            }

            Consume(TagCloseStart);       // ! </

            var (closingModuleName, closingElementName) = ReadElementName();
                
            Consume(TagEnd);              // ! >                
        }

        LeaveMode(Mode.Element);

        return new ElementSyntax(moduleName, elementName, args, children ?? [], isSelfClosed);
    }

    [SkipLocalsInit]
    public ISyntaxNode ReadElementChild()
    {
        switch (Current.Kind)
        {
            case BraceOpen : return ReadBlock();   // {
            case TagStart  : return ReadElement(); // <
        }

        var sb = new ValueStringBuilder(stackalloc char[16]);
            
        while (!IsOneOf(TagStart, TagCloseStart, EOF) && !IsKind(BraceOpen))
        {            
            sb.Append(Current.Text);

            if (Current.Trailing is not null)
            {
                sb.Append(Current.Trailing);
            }

            Consume();
        }

        return new TextNodeSyntax(sb.ToString());
    }

    private (string?, string) ReadElementName()
    {
        string name = ReadName();
        string? module = null;
            
        if (ConsumeIf(Colon))
        {
            module = name;
            name = ReadName();
        }

        return (module, name);
    }

    #endregion

    #region Primary Expressions

    public ISyntaxNode MaybeTuple()
    {
        if (InMode(Mode.Parenthesis))
        {
            var left = MaybeMemberAccess();

            if (ConsumeIf(Colon)) // :
            {
                EnterMode(Mode.Arguments);

                var value = ReadExpression();

                var element = new TupleElementSyntax((Symbol)left, value);

                var result = FinishReadingTuple(element);

                LeaveMode(Mode.Arguments);

                LeaveMode(Mode.Parenthesis);

                return result;
            }

            else if (IsKind(Comma))
            {
                left = FinishReadingTuple(left);

                LeaveMode(Mode.Parenthesis);
            }

            return left;
        }

        return MaybeRange();
    }

    // a..z
    // A..z
    // 1..3
    // 1..100
    // i..<10
    // i..i32.max
    public ISyntaxNode MaybeRange()
    {
        // TODO: Move to binary operators

        var left = MaybeType();

        if (ConsumeIf(DotDotDot)) // ? ...
        {
            return new RangeExpressionSyntax(left, ReadExpression(), RangeFlags.Inclusive);
        }
        else if (ConsumeIf(HalfOpenRange)) // ..<
        {
            return new RangeExpressionSyntax(left, ReadExpression(), RangeFlags.HalfOpen);
        }
            
        return left;
    }

    // {name} {type|event|record|protocol|module}
    // {name} { Object }
    public ISyntaxNode MaybeType()
    {
        var left = MaybeMemberAccess();

        if (left is Symbol name)
        {
            var symbolList = new ListBuilder<Symbol>();

            if (IsKind(Comma) && InMode(Mode.Root)) // ? ,
            {
                symbolList.Add(name);

                while (ConsumeIf(Comma))
                {
                    symbolList.Add(ReadTypeSymbol());
                }
            }

            switch (Current.Kind)
            {      
                case Colon when InMode(Mode.Root):
                    return symbolList.Count > 0
                        ? ReadCompoundTypeDeclaration(symbolList.ToArray())
                        : ReadTypeDeclaration(name);
                       
                case Unit   : return ReadUnitDeclaration(name);
                case Module : return ReadModule(name);

                // Types
                case Event  :
                case Record :
                case Struct :
                case Class  :
                case Role   :
                case Actor  :
                    return symbolList.Count > 0
                        ? ReadCompoundTypeDeclaration(symbolList.ToArray())
                        : ReadTypeDeclaration(name);  // type : hello

                case Implementation : return ReadImplementation(name);
                case Protocol       : return ReadProtocol(name);
                case Function       : return ReadFunctionDeclaration(name);
            }
        }

        return left;
    }

    int depth = 0;
    int count = 0;

    // A |> B   A.Call
    // A.B
    // Physics::Constants.e
    public ISyntaxNode MaybeMemberAccess()
    {
        var left = ReadPrimary();

        // throw new Exception($"{left}/{left.GetType().Name}/{Current.Kind}");

        // Maybe member access
     
        while (_reader.Current.Kind is Dot or BracketOpen or ParenthesisOpen or PipeForward)
        {
            if (IsKind(PipeForward))
            {
                Consume(PipeForward); // |>

                var call = ReadCall(left);

                call.IsPiped = true;

                left = call;
            }
            else if (IsKind(ParenthesisOpen)) // ? (
            {
                if (left is TypeSymbol type)
                {
                    left = ReadObjectInitializer(type);
                }
                else
                {
                    left = ReadCall(null, (Symbol)left);
                }
            }
            else if (ConsumeIf(BracketOpen)) // ? [
            {
                var args = ReadArguments();

                Consume(BracketClose);

                left = new IndexAccessExpressionSyntax(left, args);
            }
            else if (ConsumeIf(Dot))         // ? .
            {
                PropertySymbol name = ReadMemberSymbol();

                left = IsKind(ParenthesisOpen)  // ? (
                    ? new CallExpressionSyntax(left, name, arguments: ReadArguments())
                    : new MemberAccessExpressionSyntax(left, name);
            }
        }

        return left;
    }

    public ISyntaxNode ReadPrimary()
    {
        if (depth > 1)
        {
            throw new UnexpectedTokenException($"token not read. current mode {_modes.Peek()}. depth: {depth}", Current);
        }

        // Operators
        if (IsKind(Op))
        {
            var op = Consume(Op);

            if (_environment.Operators[Prefix, op] is Operator unaryOperator)
            {
                return ReadUnary(unaryOperator);
            }

            throw new Exception($"Unexpected operator. Was {op}");
        }

        if (ConsumeIf(ParenthesisOpen))       // ? (
        {
            EnterMode(Mode.Parenthesis);

            var position = Current.Start;

            var left = ReadExpression();

            if (ConsumeIf(ParenthesisClose))  // ? )
            {
                LeaveMode(Mode.Parenthesis);

                // Check if there's a unit
                // e.g. (5 / 5) m

                if (_reader.Current.Kind is Identifier && _reader.Current.Start.Line == position.Line)
                {
                    var (unitName, unitPower) = ReadUnitSymbol();

                    return new QuantitySyntax(left, unitName, unitPower);
                }
            }

            return left;
        }            

        switch (_reader.Current.Kind)
        {
            case This:
            case Identifier:
                depth = 0;

                // read member or type...

                Symbol symbol = char.IsUpper(_reader.Current.Text![0])
                    ? ReadTypeSymbol()
                    : ReadMemberSymbol();

                if (IsKind(LambdaOperator) && InMode(Mode.Arguments))  // ? =>
                {
                    return ReadAnonymousFunctionDeclaration(symbol);
                }

                return symbol;

            case Number      : depth = 0; return ReadNumber();
            case BracketOpen : depth = 0; return ReadArrayInitializer();
            case Quote       : depth = 0; return ReadStringLiteral();
            case Dollar      : depth = 0; return ReadDollarSymbol();
        }

        depth++;

        return ReadExpression();
    }

    #endregion

    #region Calls
  
    // a =>
    // (arg1, arg2, arg3)
    // (a: 1, a: 2, a: 3)

    private ArgumentSyntax[] ReadArguments()
    {
        if (!MoreArguments()) return [];

        bool parenthesized = ConsumeIf(ParenthesisOpen);   // ? (

        if (parenthesized && ConsumeIf(ParenthesisClose)) // ? )
        {
            return [];
        }

        EnterMode(Mode.Arguments);

        ArgumentSyntax[] args;

        var arg = ReadArgument();

        if (IsKind(Comma))
        {
            var list = new ListBuilder<ArgumentSyntax>();

            list.Add(arg);

            while (ConsumeIf(Comma))
            {
                list.Add(ReadArgument());
            }

            args = list.ToArray();
        }
        else
        {
            args = [ arg ];
        }

        if (parenthesized)
        {
            Consume(ParenthesisClose); // ! )
        }

        LeaveMode(Mode.Arguments);

        return args;
    }

    public ArgumentSyntax ReadArgument()
    {
        ISyntaxNode first = ReadExpression();

        Symbol? name;
        ISyntaxNode value;

        if (ConsumeIf(Colon))
        {
            name = (Symbol)first;
            value = ReadExpression();
        }
        else
        {
            name = null;
            value = first;
        }

        return new ArgumentSyntax(name, value);
    }

    private bool MoreArguments()
    {
        return Current.Kind switch
        {
            EOF              or 
            Bar              or // |
            PipeForward      or // |>
            ParenthesisClose or // )
            BracketClose     or // ]
            Semicolon           // ;
               => false,
            _  => true
        };
    }

    public CallExpressionSyntax ReadCall(ISyntaxNode callee)
    {
        return ReadCall(callee, functionName: ReadMethodSymbol());
    }

    // TODO: Scope read if arg count is fixed ?

    public CallExpressionSyntax ReadCall(ISyntaxNode? callee, Symbol functionName)
    {
        return new CallExpressionSyntax(
            callee    : callee,
            name      : functionName,
            arguments : ReadArguments()
        );
    }

    #endregion

    #region Helpers

    public bool IsEof => _reader.IsEof;

    public Token Current => _reader.Current;

    Token Consume() => _reader.Consume();

    Token Consume(TokenKind kind) => _reader.Consume(kind);

    T Consume<T>(TokenKind kind)
        where T : INumber<T>
    {
        var token = _reader.Consume(kind);

        return T.Parse(token.Text, CultureInfo.InvariantCulture);
    }

    bool ConsumeIf(TokenKind kind) => _reader.ConsumeIf(kind);

    bool ConsumeIf(string text)
    {
        if (_reader.Current.Equals(text))
        {
            _reader.Consume();

            return true;
        }

        return false;
    }

    bool ConsumeIf(char text)
    {
        if (_reader.Current.Text is { Length: 1 } && 
            _reader.Current.Text[0] == text)
        {
            _reader.Consume();

            return true;
        }

        return false;
    }

    bool IsOneOf(TokenKind a, TokenKind b) => 
            _reader.Current.Kind == a
        || _reader.Current.Kind == b;

    bool IsOneOf(TokenKind a, TokenKind b, TokenKind c) =>
            _reader.Current.Kind == a 
        || _reader.Current.Kind == b 
        || _reader.Current.Kind == c;

    bool IsKind(TokenKind kind) => _reader.Current.Kind == kind;

    #endregion

    [DoesNotReturn]
    private void ThrowExceededCallDepth()
    {
        throw new Exception($"Exceeded call depth reading {_reader.Current.Kind}");
    }
}