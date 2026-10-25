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
    private static readonly DiagnosticDescriptor DuplicateHandler = new(
        id: "KPM001",
        title: "Multiple handlers for the same request",
        messageFormat: "Request '{0}' has multiple handlers ({1}); AddMediatorHandlers registers only '{2}' for it",
        category: "KetPsi.Mediator",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Collect all handler implementations (for DI registration in the project that defines them)
        var handlers = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => IsCandidateType(node),
                transform: static (ctx, ct) => GetHandlerInfos(ctx, ct))
            .SelectMany(static (h, _) => h)
            .Collect();

        // Collect all pipeline behavior implementations
        var behaviors = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => IsCandidateType(node),
                transform: static (ctx, ct) => GetBehaviorInfos(ctx, ct))
            .SelectMany(static (b, _) => b)
            .Collect();

        // Collect all mediator.Send call sites (independent of whether handlers live in this compilation)
        var sendCalls = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => IsSendInvocation(node),
                transform: static (ctx, ct) => GetSendCallInfo(ctx, ct))
            .Where(static c => c is not null)
            .Select(static (c, _) => c!)
            .Collect();

        // Only the assembly name is needed – depending on the whole compilation would re-run Emit on every edit.
        var assemblyName = context.CompilationProvider
            .Select(static (c, _) => c.AssemblyName ?? string.Empty);

        var data = assemblyName
            .Combine(handlers)
            .Combine(behaviors)
            .Combine(sendCalls);

        context.RegisterSourceOutput(data, static (spc, data) =>
        {
            var (((assembly, handlerList), behaviorList), callList) = data;
            Emit(spc, assembly, handlerList, behaviorList, callList);
        });
    }

    private static bool IsCandidateType(SyntaxNode node) =>
        node is TypeDeclarationSyntax { BaseList: not null }
        and (ClassDeclarationSyntax or RecordDeclarationSyntax or StructDeclarationSyntax);

    private static bool IsSendInvocation(SyntaxNode node)
    {
        if (node is not InvocationExpressionSyntax invocation)
            return false;

        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            return false;

        return memberAccess.Name.Identifier.Text == "Send";
    }

    private static ImmutableArray<HandlerInfo> GetHandlerInfos(GeneratorSyntaxContext context, CancellationToken ct)
    {
        var symbol = context.SemanticModel.GetDeclaredSymbol(context.Node, ct) as INamedTypeSymbol;
        if (symbol is null || !IsRegistrable(symbol, context.SemanticModel.Compilation))
            return ImmutableArray<HandlerInfo>.Empty;

        var handlerType = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var result = ImmutableArray.CreateBuilder<HandlerInfo>();

        // A single class may handle several requests – register every handler interface it implements.
        foreach (var iface in symbol.AllInterfaces)
        {
            if (iface is not { IsGenericType: true })
                continue;

            var def = iface.OriginalDefinition.ToDisplayString();
            var kind = def switch
            {
                "KetPsi.Mediator.ICommandHandler<TCommand>" => HandlerKind.Command,
                "KetPsi.Mediator.ICommandHandler<TCommand, TResponse>" => HandlerKind.CommandWithResponse,
                "KetPsi.Mediator.IQueryHandler<TQuery, TResponse>" => HandlerKind.Query,
                _ => (HandlerKind?)null,
            };

            if (kind is null)
                continue;

            result.Add(new HandlerInfo(
                HandlerType: handlerType,
                RequestType: iface.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                ResponseType: kind == HandlerKind.Command
                    ? null
                    : iface.TypeArguments[1].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                Kind: kind.Value));
        }

        return result.ToImmutable();
    }

    private static ImmutableArray<BehaviorInfo> GetBehaviorInfos(GeneratorSyntaxContext context, CancellationToken ct)
    {
        var symbol = context.SemanticModel.GetDeclaredSymbol(context.Node, ct) as INamedTypeSymbol;
        if (symbol is null || !IsRegistrable(symbol, context.SemanticModel.Compilation))
            return ImmutableArray<BehaviorInfo>.Empty;
        // Skip nested types – they are typically test helpers not meant for auto-registration
        if (symbol.ContainingType is not null)
            return ImmutableArray<BehaviorInfo>.Empty;

        var behaviorType = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var result = ImmutableArray.CreateBuilder<BehaviorInfo>();

        foreach (var iface in symbol.AllInterfaces)
        {
            if (iface is not { IsGenericType: true, TypeArguments.Length: 2 })
                continue;

            if (iface.OriginalDefinition.ToDisplayString() != "KetPsi.Mediator.IPipelineBehavior<TRequest, TResponse>")
                continue;

            result.Add(new BehaviorInfo(
                BehaviorType: behaviorType,
                RequestType: iface.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                ResponseType: iface.TypeArguments[1].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
        }

        return result.ToImmutable();
    }

    /// <summary>
    /// A type can be emitted as <c>typeof(T)</c> in generated DI code only if it is concrete,
    /// closed (no type parameters on it or its containers) and accessible from the assembly.
    /// Open generic behaviors are therefore skipped – they are registered manually as open generics.
    /// </summary>
    private static bool IsRegistrable(INamedTypeSymbol symbol, Compilation compilation)
    {
        if (symbol.IsAbstract || symbol.IsStatic || !symbol.CanBeReferencedByName)
            return false;

        for (var t = symbol; t is not null; t = t.ContainingType)
        {
            if (t.TypeParameters.Length > 0)
                return false;
        }

        return compilation.IsSymbolAccessibleWithin(symbol, compilation.Assembly);
    }

    /// <summary>
    /// Whether <paramref name="type"/> can be named from a generated file in this compilation.
    /// </summary>
    private static bool IsNameable(ITypeSymbol type, Compilation compilation)
    {
        switch (type)
        {
            case ITypeParameterSymbol:
            case IPointerTypeSymbol:
            case IFunctionPointerTypeSymbol:
            case IErrorTypeSymbol:
                return false;
            case IArrayTypeSymbol array:
                return IsNameable(array.ElementType, compilation);
            case INamedTypeSymbol named:
                if (named.IsAnonymousType || !compilation.IsSymbolAccessibleWithin(named, compilation.Assembly))
                    return false;
                foreach (var arg in named.TypeArguments)
                {
                    if (!IsNameable(arg, compilation))
                        return false;
                }
                return named.ContainingType is null || IsNameable(named.ContainingType, compilation);
            default:
                return type.TypeKind == TypeKind.Dynamic;
        }
    }

    private static SendCallInfo? GetSendCallInfo(GeneratorSyntaxContext context, CancellationToken ct)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        var memberAccess = (MemberAccessExpressionSyntax)invocation.Expression;

        var methodSymbol = context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol as IMethodSymbol;
        if (methodSymbol is null || methodSymbol.Parameters.Length == 0)
            return null;

        // Must be IMediator.Send or Mediator.Send
        var containingType = methodSymbol.ContainingType?.ToDisplayString();
        if (containingType is not ("KetPsi.Mediator.IMediator" or "KetPsi.Mediator.Mediator"))
            return null;

        var filePath = invocation.SyntaxTree.FilePath;
        if (string.IsNullOrEmpty(filePath))
            return null;

        var requestArg = FindArgument(invocation.ArgumentList, methodSymbol.Parameters[0].Name);
        if (requestArg is null)
            return null;

        var requestType = context.SemanticModel.GetTypeInfo(requestArg.Expression, ct).Type;
        if (requestType is null || requestType.TypeKind == TypeKind.Error)
            return null;

        // Only intercept when the request type is a concrete, non-abstract class/struct
        if (requestType is not INamedTypeSymbol
            || requestType.TypeKind is TypeKind.Interface
            || requestType.IsAbstract)
            return null;

        var compilation = context.SemanticModel.Compilation;
        if (!IsNameable(requestType, compilation))
            return null;

        // The overload that was bound decides the interceptor signature: Send(ICommand) vs Send<TResponse>(IRequest<TResponse>).
        HandlerKind kind;
        ITypeSymbol? response = null;

        if (!methodSymbol.IsGenericMethod)
        {
            kind = HandlerKind.Command;
        }
        else
        {
            response = methodSymbol.TypeArguments[0];
            if (!IsNameable(response, compilation))
                return null;

            HandlerKind? match = null;
            foreach (var iface in requestType.AllInterfaces)
            {
                if (iface.TypeArguments.Length != 1
                    || !SymbolEqualityComparer.Default.Equals(iface.TypeArguments[0], response))
                    continue;

                var def = iface.OriginalDefinition.ToDisplayString();
                if (def == "KetPsi.Mediator.IQuery<TResponse>")
                {
                    match = HandlerKind.Query;
                    break;
                }

                if (def == "KetPsi.Mediator.ICommand<TResponse>")
                {
                    match = HandlerKind.CommandWithResponse;
                    break;
                }
            }

            // No exact IQuery<T>/ICommand<T> match (e.g. covariant Send<object>(query of string)):
            // leave the call to the runtime fallback, which handles variance.
            if (match is null)
                return null;

            kind = match.Value;
        }

        var location = memberAccess.Name.GetLocation().GetLineSpan();

        return new SendCallInfo(
            FilePath: filePath,
            Line: location.StartLinePosition.Line + 1,
            Character: location.StartLinePosition.Character + 1,
            RequestType: requestType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            ResponseType: response?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            ReceiverIsMediatorClass: containingType == "KetPsi.Mediator.Mediator",
            Kind: kind);
    }

    private static ArgumentSyntax? FindArgument(ArgumentListSyntax arguments, string parameterName)
    {
        foreach (var arg in arguments.Arguments)
        {
            if (arg.NameColon?.Name.Identifier.ValueText == parameterName)
                return arg;
        }

        return arguments.Arguments.Count > 0 && arguments.Arguments[0].NameColon is null
            ? arguments.Arguments[0]
            : null;
    }

    private static void Emit(
        SourceProductionContext context,
        string assemblyName,
        ImmutableArray<HandlerInfo> handlers,
        ImmutableArray<BehaviorInfo> behaviors,
        ImmutableArray<SendCallInfo> calls)
    {
        // Always emit the InterceptsLocation attribute (file-local / internal – no cross-assembly clash)
        context.AddSource("InterceptsLocationAttribute.g.cs", SourceText.From("""
// <auto-generated/>
#pragma warning disable
namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    internal sealed class InterceptsLocationAttribute : Attribute
    {
        public InterceptsLocationAttribute(string filePath, int line, int character) { }
    }
}
""", Encoding.UTF8));

        // Only emit DI registration when this compilation actually defines handlers or behaviors.
        // This prevents CS0121/CS0436 when multiple projects in a solution all reference the generator.
        if (!handlers.IsDefaultOrEmpty || !behaviors.IsDefaultOrEmpty)
        {
            ReportDuplicateHandlers(context, handlers);
            EmitHandlerRegistration(context, assemblyName, handlers, behaviors);
        }

        // Interceptors are emitted purely from call sites – handlers may live in another assembly.
        if (!calls.IsDefaultOrEmpty)
        {
            EmitInterceptors(context, calls);
        }
    }

    private static void ReportDuplicateHandlers(SourceProductionContext context, ImmutableArray<HandlerInfo> handlers)
    {
        var groups = handlers
            .Distinct()
            .GroupBy(h => (h.Kind, h.RequestType, h.ResponseType))
            .Where(g => g.Count() > 1);

        foreach (var group in groups)
        {
            var names = group.Select(h => h.HandlerType).ToArray();
            context.ReportDiagnostic(Diagnostic.Create(
                DuplicateHandler,
                Location.None,
                group.Key.RequestType,
                string.Join(", ", names),
                names[0]));
        }
    }

    private static void EmitHandlerRegistration(
        SourceProductionContext context,
        string assemblyName,
        ImmutableArray<HandlerInfo> handlers,
        ImmutableArray<BehaviorInfo> behaviors)
    {
        var assemblyMethod = $"Add{ToIdentifier(assemblyName)}MediatorHandlers";

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#pragma warning disable");
        sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
        sb.AppendLine("using Microsoft.Extensions.DependencyInjection.Extensions;");
        sb.AppendLine();
        sb.AppendLine("namespace KetPsi.Mediator");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Generated extension that registers IMediator, all discovered handlers,");
        sb.AppendLine($"    /// and all discovered closed pipeline behaviors in {EscapeXml(assemblyName)}.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    public static class GeneratedMediatorServiceCollectionExtensions");
        sb.AppendLine("    {");
        sb.AppendLine("        /// <summary>");
        sb.AppendLine("        /// Registers the mediator, all handlers, and closed pipeline behaviors.");
        sb.AppendLine($"        /// When several referenced projects contain handlers, call <see cref=\"{assemblyMethod}\"/>");
        sb.AppendLine("        /// (and its counterparts in the other projects) instead, because this name is then ambiguous.");
        sb.AppendLine("        /// </summary>");
        sb.AppendLine("        /// <param name=\"services\">The service collection to configure.</param>");
        sb.AppendLine("        /// <param name=\"lifetime\">");
        sb.AppendLine("        /// Lifetime for IMediator, handlers, and behaviors. Defaults to Scoped.");
        sb.AppendLine("        /// Use Scoped for per-request web applications.");
        sb.AppendLine("        /// </param>");
        sb.AppendLine("        public static IServiceCollection AddMediatorHandlers(");
        sb.AppendLine("            this IServiceCollection services,");
        sb.AppendLine("            ServiceLifetime lifetime = ServiceLifetime.Scoped)");
        sb.AppendLine($"            => {assemblyMethod}(services, lifetime);");
        sb.AppendLine();
        sb.AppendLine("        /// <summary>");
        sb.AppendLine($"        /// Registers the mediator plus the handlers and closed pipeline behaviors declared in {EscapeXml(assemblyName)}.");
        sb.AppendLine("        /// Safe to call once per handler project in multi-project solutions.");
        sb.AppendLine("        /// </summary>");
        sb.AppendLine("        /// <param name=\"services\">The service collection to configure.</param>");
        sb.AppendLine("        /// <param name=\"lifetime\">Lifetime for IMediator, handlers, and behaviors. Defaults to Scoped.</param>");
        sb.AppendLine($"        public static IServiceCollection {assemblyMethod}(");
        sb.AppendLine("            this IServiceCollection services,");
        sb.AppendLine("            ServiceLifetime lifetime = ServiceLifetime.Scoped)");
        sb.AppendLine("        {");
        sb.AppendLine("            services.TryAdd(ServiceDescriptor.Describe(");
        sb.AppendLine("                typeof(global::KetPsi.Mediator.IMediator),");
        sb.AppendLine("                typeof(global::KetPsi.Mediator.Mediator),");
        sb.AppendLine("                lifetime));");
        sb.AppendLine();

        var registeredConcrete = new HashSet<string>();
        foreach (var h in handlers.Distinct())
        {
            // Concrete type + interface forwarding share the same lifetime and instance.
            if (registeredConcrete.Add(h.HandlerType))
                sb.AppendLine($"            services.TryAdd(ServiceDescriptor.Describe(typeof({h.HandlerType}), typeof({h.HandlerType}), lifetime));");

            switch (h.Kind)
            {
                case HandlerKind.Command:
                    sb.AppendLine($"            services.TryAdd(ServiceDescriptor.Describe(typeof(global::KetPsi.Mediator.ICommandHandler<{h.RequestType}>), sp => sp.GetRequiredService<{h.HandlerType}>(), lifetime));");
                    break;
                case HandlerKind.CommandWithResponse:
                    sb.AppendLine($"            services.TryAdd(ServiceDescriptor.Describe(typeof(global::KetPsi.Mediator.ICommandHandler<{h.RequestType}, {h.ResponseType}>), sp => sp.GetRequiredService<{h.HandlerType}>(), lifetime));");
                    break;
                case HandlerKind.Query:
                    sb.AppendLine($"            services.TryAdd(ServiceDescriptor.Describe(typeof(global::KetPsi.Mediator.IQueryHandler<{h.RequestType}, {h.ResponseType}>), sp => sp.GetRequiredService<{h.HandlerType}>(), lifetime));");
                    break;
            }
        }

        // Closed behaviors only – open generics stay explicit so tests remain deterministic.
        foreach (var b in behaviors.Distinct())
        {
            // Register only via the open IPipelineBehavior<> slot so GetServices returns each behavior once.
            sb.AppendLine($"            services.TryAddEnumerable(ServiceDescriptor.Describe(typeof(global::KetPsi.Mediator.IPipelineBehavior<{b.RequestType}, {b.ResponseType}>), typeof({b.BehaviorType}), lifetime));");
        }

        sb.AppendLine("            return services;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        context.AddSource("GeneratedMediatorServiceCollectionExtensions.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
    }

    private static void EmitInterceptors(
        SourceProductionContext context,
        ImmutableArray<SendCallInfo> calls)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#pragma warning disable");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Runtime.CompilerServices;");
        sb.AppendLine("using System.Threading;");
        sb.AppendLine("using System.Threading.Tasks;");
        sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
        sb.AppendLine();
        sb.AppendLine("namespace KetPsi.Mediator");
        sb.AppendLine("{");
        sb.AppendLine("    file static class MediatorInterceptors");
        sb.AppendLine("    {");

        int id = 0;
        foreach (var call in calls)
        {
            id++;
            string interceptorName = $"Intercept_Send_{id}";

            // The interceptor's 'this' parameter must match the receiver type of the intercepted method.
            string receiverType = call.ReceiverIsMediatorClass
                ? "global::KetPsi.Mediator.Mediator"
                : "global::KetPsi.Mediator.IMediator";

            sb.AppendLine($"        [InterceptsLocation(@\"{EscapeVerbatim(call.FilePath)}\", {call.Line}, {call.Character})]");

            if (call.Kind is HandlerKind.Query or HandlerKind.CommandWithResponse)
            {
                string handlerResolve = call.Kind == HandlerKind.Query
                    ? $"global::KetPsi.Mediator.IQueryHandler<{call.RequestType}, {call.ResponseType}>"
                    : $"global::KetPsi.Mediator.ICommandHandler<{call.RequestType}, {call.ResponseType}>";

                string executeMethod = call.Kind == HandlerKind.Query
                    ? "ExecuteQuery"
                    : "ExecuteCommand";

                sb.AppendLine($"        public static ValueTask<{call.ResponseType}> {interceptorName}(");
                sb.AppendLine($"            this {receiverType} mediator,");
                sb.AppendLine($"            global::KetPsi.Mediator.IRequest<{call.ResponseType}> request,");
                sb.AppendLine($"            CancellationToken cancellationToken = default)");
                sb.AppendLine("        {");
                sb.AppendLine("            // Custom IMediator implementations (decorators, mocks) keep their own behavior.");
                sb.AppendLine("            if (mediator is not global::KetPsi.Mediator.Mediator m)");
                sb.AppendLine($"                return mediator.Send<{call.ResponseType}>(request, cancellationToken);");
                sb.AppendLine("            ArgumentNullException.ThrowIfNull(request);");
                sb.AppendLine($"            var concrete = ({call.RequestType})request;");
                sb.AppendLine($"            var handler = m.GetRequiredService<{handlerResolve}>();");
                sb.AppendLine($"            var behaviors = m.GetServices<global::KetPsi.Mediator.IPipelineBehavior<{call.RequestType}, {call.ResponseType}>>();");
                sb.AppendLine($"            return global::KetPsi.Mediator.PipelineExecutor.{executeMethod}(concrete, behaviors, handler, cancellationToken);");
                sb.AppendLine("        }");
            }
            else
            {
                sb.AppendLine($"        public static ValueTask {interceptorName}(");
                sb.AppendLine($"            this {receiverType} mediator,");
                sb.AppendLine($"            global::KetPsi.Mediator.ICommand request,");
                sb.AppendLine($"            CancellationToken cancellationToken = default)");
                sb.AppendLine("        {");
                sb.AppendLine("            // Custom IMediator implementations (decorators, mocks) keep their own behavior.");
                sb.AppendLine("            if (mediator is not global::KetPsi.Mediator.Mediator m)");
                sb.AppendLine("                return mediator.Send(request, cancellationToken);");
                sb.AppendLine("            ArgumentNullException.ThrowIfNull(request);");
                sb.AppendLine($"            var concrete = ({call.RequestType})request;");
                sb.AppendLine($"            var handler = m.GetRequiredService<global::KetPsi.Mediator.ICommandHandler<{call.RequestType}>>();");
                sb.AppendLine($"            var behaviors = m.GetServices<global::KetPsi.Mediator.IPipelineBehavior<{call.RequestType}, global::KetPsi.Mediator.Unit>>();");
                sb.AppendLine("            return global::KetPsi.Mediator.PipelineExecutor.ExecuteUnit(concrete, behaviors, handler, cancellationToken);");
                sb.AppendLine("        }");
            }

            sb.AppendLine();
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");

        context.AddSource("MediatorInterceptors.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
    }

    // The path is emitted inside a verbatim string (@"..."): backslashes stay as-is, quotes are doubled.
    private static string EscapeVerbatim(string path) => path.Replace("\"", "\"\"");

    private static string EscapeXml(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>
    /// Turns an assembly name such as <c>MyApp.Orders.Application</c> into <c>MyAppOrdersApplication</c>.
    /// </summary>
    private static string ToIdentifier(string assemblyName)
    {
        var sb = new StringBuilder();
        bool upperNext = true;

        foreach (var c in assemblyName)
        {
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                sb.Append(upperNext ? char.ToUpperInvariant(c) : c);
                upperNext = false;
            }
            else
            {
                upperNext = true;
            }
        }

        if (sb.Length == 0 || char.IsDigit(sb[0]))
            sb.Insert(0, '_');

        return sb.ToString();
    }
}

internal enum HandlerKind
{
    Command,
    CommandWithResponse,
    Query
}

internal sealed record HandlerInfo(
    string HandlerType,
    string RequestType,
    string? ResponseType,
    HandlerKind Kind);

internal sealed record SendCallInfo(
    string FilePath,
    int Line,
    int Character,
    string RequestType,
    string? ResponseType,
    bool ReceiverIsMediatorClass,
    HandlerKind Kind);

internal sealed record BehaviorInfo(
    string BehaviorType,
    string RequestType,
    string ResponseType);
