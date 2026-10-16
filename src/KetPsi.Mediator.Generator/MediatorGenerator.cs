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
        // Collect all handler implementations (for DI registration in the project that defines them)
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

        // Collect all mediator.Send call sites (independent of whether handlers live in this compilation)
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
                    HandlerType: symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    RequestType: iface.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    ResponseType: null,
                    Kind: HandlerKind.Command);
            }

            if (def == "KetPsi.Mediator.ICommandHandler<TCommand, TResponse>")
            {
                return new HandlerInfo(
                    HandlerType: symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    RequestType: iface.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    ResponseType: iface.TypeArguments[1].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    Kind: HandlerKind.CommandWithResponse);
            }

            if (def == "KetPsi.Mediator.IQueryHandler<TQuery, TResponse>")
            {
                return new HandlerInfo(
                    HandlerType: symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    RequestType: iface.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    ResponseType: iface.TypeArguments[1].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    Kind: HandlerKind.Query);
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
                    BehaviorType: symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    RequestType: iface.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    ResponseType: iface.TypeArguments[1].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
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

        // Must be IMediator.Send or Mediator.Send
        var containingType = methodSymbol.ContainingType?.ToDisplayString();
        if (containingType is not ("KetPsi.Mediator.IMediator" or "KetPsi.Mediator.Mediator"))
            return null;

        if (invocation.ArgumentList.Arguments.Count < 1)
            return null;

        var requestArg = invocation.ArgumentList.Arguments[0].Expression;
        var requestTypeInfo = context.SemanticModel.GetTypeInfo(requestArg, ct);
        var requestType = requestTypeInfo.Type;
        if (requestType is null || requestType.TypeKind == TypeKind.Error)
            return null;

        // Only intercept when the request type is a concrete, non-abstract class/struct
        if (requestType is ITypeParameterSymbol
            || requestType.TypeKind is TypeKind.Interface or TypeKind.TypeParameter
            || requestType.IsAbstract)
            return null;

        // Determine HandlerKind from the request type's interfaces (works across assemblies)
        HandlerKind? kind = null;
        string? responseFromInterface = null;

        foreach (var iface in requestType.AllInterfaces)
        {
            var def = iface.OriginalDefinition.ToDisplayString();

            if (def == "KetPsi.Mediator.IQuery<TResponse>")
            {
                kind = HandlerKind.Query;
                responseFromInterface = iface.TypeArguments[0]
                    .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                break;
            }

            if (def == "KetPsi.Mediator.ICommand<TResponse>")
            {
                kind = HandlerKind.CommandWithResponse;
                responseFromInterface = iface.TypeArguments[0]
                    .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                break;
            }

            if (def == "KetPsi.Mediator.ICommand")
            {
                kind = HandlerKind.Command;
                // keep looking – a type might also implement ICommand<TResponse>
            }
        }

        // Prefer CommandWithResponse over plain Command when both match
        if (kind is null)
            return null;

        var location = memberAccess.Name.GetLocation().GetLineSpan();
        var filePath = invocation.SyntaxTree.FilePath;

        bool isGeneric = methodSymbol.IsGenericMethod;
        string? responseType = null;
        if (isGeneric && methodSymbol.TypeArguments.Length == 1)
        {
            responseType = methodSymbol.TypeArguments[0]
                .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        }
        else if (responseFromInterface is not null)
        {
            responseType = responseFromInterface;
            isGeneric = true;
        }

        return new SendCallInfo(
            FilePath: filePath,
            Line: location.StartLinePosition.Line + 1,
            Character: location.StartLinePosition.Character + 1,
            RequestType: requestType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            ResponseType: responseType,
            IsGeneric: isGeneric,
            HasCancellationToken: invocation.ArgumentList.Arguments.Count > 1,
            Kind: kind.Value);
    }

    private static void Emit(
        SourceProductionContext context,
        Compilation compilation,
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
            EmitHandlerRegistration(context, handlers, behaviors);
        }

        // Interceptors are emitted purely from call sites – handlers may live in another assembly.
        if (!calls.IsDefaultOrEmpty)
        {
            EmitInterceptors(context, calls);
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
        // Emitted only when this compilation defines handlers/behaviors, so multi-project solutions
        // that put handlers in one assembly and call sites in another do not get duplicate public types.
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Generated extension that registers IMediator, all discovered handlers,");
        sb.AppendLine("    /// and all discovered pipeline behaviors in this compilation.");
        sb.AppendLine("    /// </summary>");
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
        // file-scoped so multiple generated files / projects never clash
        sb.AppendLine("    file static class MediatorInterceptors");
        sb.AppendLine("    {");

        int id = 0;
        foreach (var call in calls)
        {
            id++;
            string interceptorName = $"Intercept_Send_{id}";

            sb.AppendLine($"        [InterceptsLocation(@\"{Escape(call.FilePath)}\", {call.Line}, {call.Character})]");

            if (call.Kind is HandlerKind.Query or HandlerKind.CommandWithResponse
                && call.ResponseType is not null)
            {
                // Signature must match IMediator.Send<TResponse>(IRequest<TResponse>, CancellationToken)
                string handlerResolve = call.Kind == HandlerKind.Query
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
    bool IsGeneric,
    bool HasCancellationToken,
    HandlerKind Kind);

internal sealed record BehaviorInfo(
    string BehaviorType,
    string RequestType,
    string ResponseType);
