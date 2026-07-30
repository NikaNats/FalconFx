using FluentAssertions;

namespace FalconFX.MatchingEngine.Tests.Unit;

public class OrderPoolTests
{
    [Fact]
    public void OrderPool_RentAndReturn_ShouldWorkWithoutMemoryLeaks()
    {
        // Arrange
        const int poolSize = 10;
        var pool = new OrderPool(poolSize);

        // Act - Rent all nodes
        var rentedIndices = new List<int>();
        for (var i = 0; i < poolSize; i++)
        {
            var idx = pool.Rent();
            idx.Should().NotBe(-1);
            rentedIndices.Add(idx);
        }

        // Assert - Pool is empty
        var exhaustedIdx = pool.Rent();
        exhaustedIdx.Should().Be(-1, "პული სრულად შევსებულია და უნდა დააბრუნოს -1");

        // Act - Return one node and rent again
        pool.Return(rentedIndices[0]);
        var newIdx = pool.Rent();

        // Assert
        newIdx.Should().Be(rentedIndices[0], "დაბრუნებული ინდექსი ხელახლა უნდა იქნას გაცემული");
    }

    [Fact]
    public void OrderPool_Reset_ShouldReinitializeAllNodes()
    {
        // Arrange
        var pool = new OrderPool(5);
        for (var i = 0; i < 5; i++) pool.Rent();

        // Act
        pool.Reset();

        // Assert
        var idx = pool.Rent();
        idx.Should().Be(0, "Reset-ის შემდეგ პირველი ინდექსი ისევ 0 უნდა იყოს");
    }
}