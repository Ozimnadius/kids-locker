using Xunit;

namespace KidsLocker.Tests;

// Tests that set the process-wide KIDSLOCKER_HOME env var must not run concurrently.
[CollectionDefinition("EnvHome", DisableParallelization = true)]
public class EnvHomeCollection { }
