using System.Reflection;

using AwesomeAssertions;

using NetArchTest.Rules;

using Opportunity.Application.Messaging;
using Opportunity.Hosting.Workers;

namespace Opportunity.ArchitectureTests;

/// <summary>
/// E06-T01 / ADR-019: RabbitMQ.Client is used only inside <c>Opportunity.Messaging</c>, and nothing that project makes
/// public exposes a RabbitMQ type, so every other project programs against the Application ports.
/// </summary>
public class MessagingIsolationTests
{
    private const string RabbitMqNamespace = "RabbitMQ.Client";

    public static TheoryData<string> NonMessagingAssemblies => new()
    {
        "Opportunity.Core",
        "Opportunity.Contracts",
        "Opportunity.Application",
        "Opportunity.Data",
        "Opportunity.Search",
        "Opportunity.Storage",
        "Opportunity.Security",
        "Opportunity.Jobs",
        "Opportunity.Import",
        "Opportunity.Rendering",
        "Opportunity.Production",
        "Opportunity.Hosting",
        "Opportunity.Api",
        "Opportunity.Migrator",
    };

    [Theory]
    [MemberData(nameof(NonMessagingAssemblies))]
    public void Only_messaging_references_rabbitmq_client(string assemblyName)
    {
        var assembly = Assembly.Load(assemblyName);

        assembly.GetReferencedAssemblies().Select(a => a.Name).Should().NotContain(RabbitMqNamespace);
        var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOn(RabbitMqNamespace).GetResult();
        result.IsSuccessful.Should().BeTrue(
            "{0} must not use RabbitMQ types; offending: {1}", assemblyName, string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Messaging_public_surface_exposes_no_rabbitmq_types()
    {
        var leaks = new List<string>();
        foreach (var type in typeof(Messaging.AssemblyMarker).Assembly.GetExportedTypes())
        {
            Check(type, $"{type.Name} base type", type.BaseType);
            foreach (var implemented in type.GetInterfaces())
            {
                Check(type, $"{type.Name} implements", implemented);
            }

            const BindingFlags Visible = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (var member in type.GetMembers(Visible | BindingFlags.NonPublic).Where(IsVisibleOutside))
            {
                switch (member)
                {
                    case MethodBase method:
                        Check(type, member.Name, (method as MethodInfo)?.ReturnType);
                        foreach (var parameter in method.GetParameters())
                        {
                            Check(type, $"{member.Name}({parameter.Name})", parameter.ParameterType);
                        }

                        break;
                    case PropertyInfo property:
                        Check(type, member.Name, property.PropertyType);
                        break;
                    case FieldInfo field:
                        Check(type, member.Name, field.FieldType);
                        break;
                    case EventInfo @event:
                        Check(type, member.Name, @event.EventHandlerType);
                        break;
                }
            }
        }

        leaks.Should().BeEmpty("RabbitMQ types must not leak outside Opportunity.Messaging");

        void Check(Type owner, string where, Type? exposed)
        {
            if (exposed is not null && Mentions(exposed))
            {
                leaks.Add($"{owner.FullName}.{where}: {exposed}");
            }
        }
    }

    [Fact]
    public void Every_work_queue_is_consumed_by_a_known_worker_type()
    {
        WorkQueues.All.Select(q => q.WorkerType).Should().OnlyContain(type => WorkerTypes.All.Contains(type));
        WorkQueues.All.Select(q => q.Name).Should().OnlyHaveUniqueItems();
    }

    private static bool IsVisibleOutside(MemberInfo member) => member switch
    {
        MethodBase method => method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly,
        FieldInfo field => field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly,
        PropertyInfo property => property.GetAccessors(nonPublic: true).Any(IsVisibleOutside),
        EventInfo @event => @event.AddMethod is { } add && IsVisibleOutside(add),
        _ => false,
    };

    private static bool Mentions(Type type)
    {
        if (type.HasElementType)
        {
            return Mentions(type.GetElementType()!);
        }

        return (type.Namespace?.StartsWith(RabbitMqNamespace, StringComparison.Ordinal) ?? false)
            || (type.IsGenericType && type.GetGenericArguments().Any(Mentions));
    }
}
