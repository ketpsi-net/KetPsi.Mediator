using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace KetPsi.Mediator.Generator;

[Generator]
public sealed class MediatorGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Collect all handler implementations
        var handlers = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList: not null },
                transform: static (ctx, ct) => GetHandlerInfo(ctx, ct))
            .Where(static h => h is not null)
            .Select(static (h, _) => h!)
            .Collect();

        // Collect all pipeline behavior implementations
        var behaviors = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList: not null },
                transform: static (ctx, ct) => GetBehaviorInfo(ctx, ct))
            .Where(static b => b is not null)
            .Select(static (b, _) => b!)
            .Collect();

        // Collect all mediator.Send call sites
        var sendCalls = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => IsSendInvocation(node),
                transform: static (ctx, ct) => GetSendCallInfo(ctx, ct))
            .Where(static c => c is not null)
            .Select(static (c, _) => c!)
            .Collect();

        var compilationAndData = context.CompilationProvider
            .Combine(handlers)
            .Combine(behaviors)
            .Combine(sendCalls);

        context.RegisterSourceOutput(compilationAndData, static (spc, data) =>
        {
            var (((compilation, handlerList), behaviorList), callList) = data;
            Emit(spc, compilation, handlerList, behaviorList, callList);
        });
    }

    private static bool IsSendInvocation(SyntaxNode node)
    {
        if (node is not InvocationExpressionSyntax invocation)
            return false;

        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            return false;

        return memberAccess.Name.Identifier.Text == "Send";
    }

    private static HandlerInfo? GetHandlerInfo(GeneratorSyntaxContext context, CancellationToken ct)
    {
        var classDecl = (ClassDeclarationSyntax)context.Node;
        var symbol = context.SemanticModel.GetDeclaredSymbol(classDecl, ct) as INamedTypeSymbol;
        if (symbol is null || symbol.IsAbstract)
            return null;

        foreach (var iface in symbol.AllInterfaces)
        {
            if (iface is not { IsGenericType: true })
                continue;

            var def = iface.OriginalDefinition.ToDisplayString();

            if (def == "KetPsi.Mediator.ICommandHandler<TCommand>")
            {
                return new HandlerInfo(
                    handlerType: symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    requestType: iface.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    responseType: null,
                    kind: HandlerKind.Command);
            }

            if (def == "KetPsi.Mediator.ICommandHandler<TCommand, TResponse>")
            {
                return new HandlerInfo(
                    handlerType: symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    requestType: iface.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    responseType: iface.TypeArguments[1].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    kind: HandlerKind.CommandWithResponse);
            }

            if (def == "KetPsi.Mediator.IQueryHandler<TQuery, TResponse>")
            {
                return new HandlerInfo(
                    handlerType: symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    requestType: iface.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    responseType: iface.TypeArguments[1].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    kind: HandlerKind.Query);
            }
        }

        return null;
    }


    private static BehaviorInfo? GetBehaviorInfo(GeneratorSyntaxContext context, CancellationToken ct)
    {
        var classDecl = (ClassDeclarationSyntax)context.Node;
        var symbol = context.SemanticModel.GetDeclaredSymbol(classDecl, ct) as INamedTypeSymbol;
        if (symbol is null || symbol.IsAbstract || !symbol.CanBeReferencedByName)
            return null;
        // Skip nested types – they are typically test helpers not meant for auto-registration
        if (symbol.ContainingType is not null)
            return null;

        foreach (var iface in symbol.AllInterfaces)
        {
            if (iface is not { IsGenericType: true, TypeArguments.Length: 2 })
                continue;

            var def = iface.OriginalDefinition.ToDisplayString();
            if (def == "KetPsi.Mediator.IPipelineBehavior<TRequest, TResponse>")
            {
                // Skip open generic behaviors (they are registered manually as open generics)
                if (iface.TypeArguments[0] is ITypeParameterSymbol || iface.TypeArguments[1] is ITypeParameterSymbol)
                    continue;

                return new BehaviorInfo(
                    behaviorType: symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    requestType: iface.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    responseType: iface.TypeArguments[1].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
            }
        }

        return null;
    }

    private static SendCallInfo? GetSendCallInfo(GeneratorSyntaxContext context, CancellationToken ct)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        var memberAccess = (MemberAccessExpressionSyntax)invocation.Expression;

        var methodSymbol = context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol as IMethodSymbol;
        if (methodSymbol is null)
            return null;

        // Must be IMediator.Send
        var containingType = methodSymbol.ContainingType?.ToDisplayString();
        if (containingType is not ("KetPsi.Mediator.IMediator" or "KetPsi.Mediator.Mediator"))
            return null;

        if (invocation.ArgumentList.Arguments.Count < 1)
            return null;

        var requestArg = invocation.ArgumentList.Arguments[0].Expression;
        var requestTypeInfo = context.SemanticModel.GetTypeInfo(requestArg, ct);
        if (requestTypeInfo.Type is null || requestTypeInfo.Type.TypeKind == TypeKind.Error)
            return null;

        // Only intercept when the request type is concrete (not an interface / type parameter)
        if (requestTypeInfo.Type is ITypeParameterSymbol or { TypeKind: TypeKind.Interface or TypeKind.TypeParameter })
            return null;

        var location = memberAccess.Name.GetLocation().GetLineSpan();
        var filePath = invocation.SyntaxTree.FilePath;

        bool isGeneric = methodSymbol.IsGenericMethod;
        string? responseType = null;
        if (isGeneric && methodSymbol.TypeArguments.Length == 1)
        {
            responseType = methodSymbol.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        }

        return new SendCallInfo(
            filePath: filePath,
            line: location.StartLinePosition.Line + 1,
            character: location.StartLinePosition.Character + 1,
            requestType: requestTypeInfo.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            responseType: responseType,
            isGeneric: isGeneric,
            hasCancellationToken: invocation.ArgumentList.Arguments.Count > 1);
    }

    private static void Emit(
        SourceProductionContext context,
        Compilation compilation,
        ImmutableArray<HandlerInfo> handlers,
        ImmutableArray<BehaviorInfo> behaviors,
        ImmutableArray<SendCallInfo> calls)
    {
       
        // Emit DI registration helper (handlers + behaviors + IMediator)
        EmitHandlerRegistration(context, handlers, behaviors);

        // Emit interceptors for concrete call sites
        if (!calls.IsDefaultOrEmpty && !handlers.IsDefaultOrEmpty)
        {
            EmitInterceptors(context, handlers, behaviors, calls);
        }
    }

    private static void EmitHandlerRegistration(
        SourceProductionContext context,
        ImmutableArray<HandlerInfo> handlers,
        ImmutableArray<BehaviorInfo> behaviors)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#pragma warning disable");
        sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
        sb.AppendLine("using Microsoft.Extensions.DependencyInjection.Extensions;");
        sb.AppendLine();
        sb.AppendLine("namespace KetPsi.Mediator");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>Generated extension that registers all discovered handlers.</summary>");
        sb.AppendLine("    public static class GeneratedMediatorServiceCollectionExtensions");
        sb.AppendLine("    {");
        sb.AppendLine("        public static IServiceCollection AddMediatorHandlers(this IServiceCollection services)");
        sb.AppendLine("        {");
        sb.AppendLine("            // Core mediator – single registration so callers only need AddMediatorHandlers()");
        sb.AppendLine("            services.TryAddSingleton<global::KetPsi.Mediator.IMediator, global::KetPsi.Mediator.Mediator>();");
        sb.AppendLine();

        foreach (var h in handlers.Distinct())
        {
            // Register the concrete type once, then map every interface to the SAME instance.
            // This guarantees that resolving the concrete type or any of its handler interfaces
            // returns the identical object (critical for tests that assert on instance state).
            sb.AppendLine($"            services.TryAddSingleton<{h.HandlerType}>();");

            switch (h.Kind)
            {
                case HandlerKind.Command:
                    sb.AppendLine($"            services.TryAddSingleton<global::KetPsi.Mediator.ICommandHandler<{h.RequestType}>>(sp => sp.GetRequiredService<{h.HandlerType}>());");
                    break;
                case HandlerKind.CommandWithResponse:
                    sb.AppendLine($"            services.TryAddSingleton<global::KetPsi.Mediator.ICommandHandler<{h.RequestType}, {h.ResponseType}>>(sp => sp.GetRequiredService<{h.HandlerType}>());");
                    break;
                case HandlerKind.Query:
                    sb.AppendLine($"            services.TryAddSingleton<global::KetPsi.Mediator.IQueryHandler<{h.RequestType}, {h.ResponseType}>>(sp => sp.GetRequiredService<{h.HandlerType}>());");
                    break;
            }
        }

        // Pipeline behaviors (open registration – resolved by request type at runtime)
        foreach (var b in behaviors.Distinct())
        {
            sb.AppendLine($"            services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(global::KetPsi.Mediator.IPipelineBehavior<{b.RequestType}, {b.ResponseType}>), typeof({b.BehaviorType})));");
            sb.AppendLine($"            services.TryAddSingleton<{b.BehaviorType}>();");
        }

        sb.AppendLine("            return services;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        context.AddSource("GeneratedMediatorServiceCollectionExtensions.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
    }

    private static void EmitInterceptors(
        SourceProductionContext context,
        ImmutableArray<HandlerInfo> handlers,
        ImmutableArray<BehaviorInfo> behaviors,
        ImmutableArray<SendCallInfo> calls)
    {
        var handlerLookup = handlers
            .GroupBy(h => h.RequestType)
            .ToDictionary(g => g.Key, g => g.First());

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#pragma warning disable");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Runtime.CompilerServices;");
        sb.AppendLine("using System.Threading;");
        sb.AppendLine("using System.Threading.Tasks;");
        sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
        sb.AppendLine();
        sb.AppendLine("""
                    // <auto-generated/>
                    #pragma warning disable
                    namespace System.Runtime.CompilerServices
                    {
                        [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
                        file sealed class InterceptsLocationAttribute : Attribute
                        {
                            public InterceptsLocationAttribute(string filePath, int line, int character) { }
                        }
                    }
                    """);
        sb.AppendLine("namespace KetPsi.Mediator");
        sb.AppendLine("{");
       
        sb.AppendLine("    file static class MediatorInterceptors");
        sb.AppendLine("    {");

        int id = 0;
        foreach (var call in calls)
        {
            if (!handlerLookup.TryGetValue(call.RequestType, out var handler))
                continue; // No handler found – leave the original call (runtime fallback)

            id++;
            string interceptorName = $"Intercept_Send_{id}";

            sb.AppendLine($"        [InterceptsLocation(@\"{Escape(call.FilePath)}\", {call.Line}, {call.Character})]");

            if (call.IsGeneric && call.ResponseType is not null)
            {
                // Signature must match IMediator.Send<TResponse>(IRequest<TResponse>, CancellationToken)
                string handlerResolve = handler.Kind == HandlerKind.Query
                    ? $"global::KetPsi.Mediator.IQueryHandler<{call.RequestType}, {call.ResponseType}>"
                    : $"global::KetPsi.Mediator.ICommandHandler<{call.RequestType}, {call.ResponseType}>";

                sb.AppendLine($"        public static ValueTask<{call.ResponseType}> {interceptorName}(");
                sb.AppendLine($"            this global::KetPsi.Mediator.IMediator mediator,");
                sb.AppendLine($"            global::KetPsi.Mediator.IRequest<{call.ResponseType}> request,");
                sb.AppendLine($"            CancellationToken cancellationToken = default)");
                sb.AppendLine("        {");
                sb.AppendLine($"            var concrete = ({call.RequestType})request;");
                sb.AppendLine($"            var m = (global::KetPsi.Mediator.Mediator)mediator;");
                sb.AppendLine($"            var handler = m.GetRequiredService<{handlerResolve}>();");
                sb.AppendLine($"            var behaviors = m.GetServices<global::KetPsi.Mediator.IPipelineBehavior<{call.RequestType}, {call.ResponseType}>>();");
                sb.AppendLine("            return global::KetPsi.Mediator.PipelineExecutor.Execute(concrete, behaviors, handler.Handle, cancellationToken);");
                sb.AppendLine("        }");
            }
            else
            {
                // Signature must match IMediator.Send(ICommand, CancellationToken)
                // Commands without response are adapted to Unit so they can share the pipeline infrastructure.
                sb.AppendLine($"        public static ValueTask {interceptorName}(");
                sb.AppendLine($"            this global::KetPsi.Mediator.IMediator mediator,");
                sb.AppendLine($"            global::KetPsi.Mediator.ICommand request,");
                sb.AppendLine($"            CancellationToken cancellationToken = default)");
                sb.AppendLine("        {");
                sb.AppendLine($"            var concrete = ({call.RequestType})request;");
                sb.AppendLine($"            var m = (global::KetPsi.Mediator.Mediator)mediator;");
                sb.AppendLine($"            var handler = m.GetRequiredService<global::KetPsi.Mediator.ICommandHandler<{call.RequestType}>>();");
                sb.AppendLine($"            var behaviors = m.GetServices<global::KetPsi.Mediator.IPipelineBehavior<{call.RequestType}, global::KetPsi.Mediator.Unit>>();");
                sb.AppendLine("            return global::KetPsi.Mediator.PipelineExecutor.ExecuteUnit(concrete, behaviors, handler.Handle, cancellationToken);");
                sb.AppendLine("        }");
            }

            sb.AppendLine();
        }

        // Helper extension on the concrete Mediator to resolve services without exposing IServiceProvider publicly
        sb.AppendLine("        // Internal helper used by interceptors");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        context.AddSource("MediatorInterceptors.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
    }

    private static string Escape(string path) => path.Replace("\\", "\\\\").Replace("\"", "\\\"");
}

internal enum HandlerKind
{
    Command,
    CommandWithResponse,
    Query
}

internal sealed class HandlerInfo
{
    public HandlerInfo(string handlerType, string requestType, string? responseType, HandlerKind kind)
    {
        HandlerType = handlerType;
        RequestType = requestType;
        ResponseType = responseType;
        Kind = kind;
    }

    public string HandlerType { get; }
    public string RequestType { get; }
    public string? ResponseType { get; }
    public HandlerKind Kind { get; }
}

internal sealed class SendCallInfo
{
    public SendCallInfo(string filePath, int line, int character, string requestType, string? responseType, bool isGeneric, bool hasCancellationToken)
    {
        FilePath = filePath;
        Line = line;
        Character = character;
        RequestType = requestType;
        ResponseType = responseType;
        IsGeneric = isGeneric;
        HasCancellationToken = hasCancellationToken;
    }

    public string FilePath { get; }
    public int Line { get; }
    public int Character { get; }
    public string RequestType { get; }
    public string? ResponseType { get; }
    public bool IsGeneric { get; }
    public bool HasCancellationToken { get; }
}

internal sealed class BehaviorInfo
{
    public BehaviorInfo(string behaviorType, string requestType, string responseType)
    {
        BehaviorType = behaviorType;
        RequestType = requestType;
        ResponseType = responseType;
    }

    public string BehaviorType { get; }
    public string RequestType { get; }
    public string ResponseType { get; }
}
