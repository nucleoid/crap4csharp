#if NET10_0
namespace Fixture; public sealed class Conditional { public int Net10() => 10; }
#else
namespace Fixture; public sealed class Conditional { public int Other() => 1; }
#endif
