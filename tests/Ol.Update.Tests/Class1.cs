using TUnit.Assertions;
using TUnit.Core;
using Ol.Update;

namespace Ol.Update.Tests;

public sealed class UpdateCommandTests
{
	[Test]
	public async Task UpdateProjectLoads()
	{
		await Assert.That(typeof(UpdateTool).Assembly.GetName().Name).IsEqualTo("Ol.Update");
	}
}
