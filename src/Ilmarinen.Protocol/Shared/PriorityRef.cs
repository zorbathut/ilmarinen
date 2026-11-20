using System;

namespace Ilmarinen.Protocol;

/// <summary>A priority as responses name it: the id stays put across a rename, the name is what an operator sees and what requests refer to it by.</summary>
public sealed record PriorityRef(Guid Id, string Name);
