namespace Driftcheck.Tests;

/// <summary>
/// Las pruebas que cambian DRIFTCHECK_OUTPUT comparten esa variable de proceso;
/// en la misma colección xUnit las ejecuta una tras otra y no se pisan las carpetas.
/// </summary>
[CollectionDefinition(Name)]
public sealed class OutputDirectoryCollection
{
    public const string Name = "DRIFTCHECK_OUTPUT";
}
