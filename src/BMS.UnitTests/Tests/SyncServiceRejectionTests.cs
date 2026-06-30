// Curated excerpt — representative unit test from the test suite.
using System.Linq.Expressions;
using AutoMapper;
using BMS.Core.Entities;
using Microsoft.EntityFrameworkCore.Query;
using BMS.Core.Enums;
using BMS.Core.Interfaces;
using BMS.Data.Services.Interfaces;
using BMS.Services.Interfaces;
using BMS.Services.Services;
using BMS.Shared.DTOs.Sync;
using FluentAssertions;
using FluentValidation;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace BMS.UnitTests.Services;

/// <summary>
/// Verifies the SyncService rejection-log state machine and sync-health classification logic
/// using mocked dependencies (unit-tested in isolation, no database).
/// </summary>
public class SyncServiceRejectionTests
{
    // ── Shared mocks ──────────────────────────────────────────────────────────────────────

    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<ILogger<SyncService>> _logger = new();
    private readonly Mock<IMapper> _mapper = new();
    private readonly Mock<IValidator<SyncSaleRequestDto>> _saleValidator = new();
    private readonly Mock<IValidator<SyncStandalonePaymentDto>> _paymentValidator = new();
    private readonly Mock<ICreditService> _creditService = new();
    private readonly Mock<ICurrentTenantService> _tenantService = new();

    private SyncService CreateSut() => new(
        _unitOfWork.Object,
        _logger.Object,
        _mapper.Object,
        _saleValidator.Object,
        _paymentValidator.Object,
        _creditService.Object,
        _tenantService.Object);

    // ── Async IQueryable helper ───────────────────────────────────────────────────────────

    private static IQueryable<T> AsyncQueryable<T>(IEnumerable<T> data)
        => new TestAsyncEnumerable<T>(data);

    // ── AcknowledgeRejectionAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task AcknowledgeRejectionAsync_WhenRejectionNotFound_Returns404()
    {
        var rejectionRepo = new Mock<IRepository<SyncRejectionLog>>();
        rejectionRepo.Setup(r => r.GetQueryable())
            .Returns(AsyncQueryable(Enumerable.Empty<SyncRejectionLog>()));
        _unitOfWork.Setup(u => u.Repository<SyncRejectionLog>()).Returns(rejectionRepo.Object);

        var sut = CreateSut();
        var result = await sut.AcknowledgeRejectionAsync(
            Guid.NewGuid(),
            new AcknowledgeRejectionDto { NewStatus = "Acknowledged" },
            Guid.NewGuid());

        result.Success.Should().BeFalse();
        result.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task AcknowledgeRejectionAsync_WhenStatusNotPending_Returns409()
    {
        var id = Guid.NewGuid();
        var rejection = new SyncRejectionLog
        {
            Id = id,
            RegisterId = Guid.NewGuid(),
            BranchId = Guid.NewGuid(),
            ReceiptNumber = "ACC-R1-000001",
            RejectionReason = "HashMismatch",
            Status = "Acknowledged"
        };

        var rejectionRepo = new Mock<IRepository<SyncRejectionLog>>();
        rejectionRepo.Setup(r => r.GetQueryable())
            .Returns(AsyncQueryable(new[] { rejection }));
        _unitOfWork.Setup(u => u.Repository<SyncRejectionLog>()).Returns(rejectionRepo.Object);

        var sut = CreateSut();
        var result = await sut.AcknowledgeRejectionAsync(
            id,
            new AcknowledgeRejectionDto { NewStatus = "Acknowledged" },
            Guid.NewGuid());

        result.Success.Should().BeFalse();
        result.StatusCode.Should().Be(409);
        result.ErrorCode.Should().Be("INVALID_STATE_TRANSITION");
    }

    [Fact]
    public async Task AcknowledgeRejectionAsync_WhenNewStatusInvalid_Returns400()
    {
        var id = Guid.NewGuid();
        var rejection = new SyncRejectionLog
        {
            Id = id,
            RegisterId = Guid.NewGuid(),
            BranchId = Guid.NewGuid(),
            ReceiptNumber = "ACC-R1-000002",
            RejectionReason = "BranchMismatch",
            Status = "Pending"
        };

        var rejectionRepo = new Mock<IRepository<SyncRejectionLog>>();
        rejectionRepo.Setup(r => r.GetQueryable())
            .Returns(AsyncQueryable(new[] { rejection }));
        _unitOfWork.Setup(u => u.Repository<SyncRejectionLog>()).Returns(rejectionRepo.Object);

        var sut = CreateSut();
        var result = await sut.AcknowledgeRejectionAsync(
            id,
            new AcknowledgeRejectionDto { NewStatus = "Deleted" },
            Guid.NewGuid());

        result.Success.Should().BeFalse();
        result.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task AcknowledgeRejectionAsync_WhenPendingAndValidStatus_TransitionsToAcknowledged()
    {
        var id = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var rejection = new SyncRejectionLog
        {
            Id = id,
            RegisterId = Guid.NewGuid(),
            BranchId = Guid.NewGuid(),
            ReceiptNumber = "ACC-R1-000003",
            RejectionReason = "HashMismatch",
            Status = "Pending"
        };

        var rejectionRepo = new Mock<IRepository<SyncRejectionLog>>();
        rejectionRepo.Setup(r => r.GetQueryable())
            .Returns(AsyncQueryable(new[] { rejection }));
        _unitOfWork.Setup(u => u.Repository<SyncRejectionLog>()).Returns(rejectionRepo.Object);

        var userRepo = new Mock<IRepository<User>>();
        userRepo.Setup(r => r.GetByIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new User { Id = userId, FullName = "Jane Doe" });
        _unitOfWork.Setup(u => u.Repository<User>()).Returns(userRepo.Object);
        _unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var sut = CreateSut();
        var result = await sut.AcknowledgeRejectionAsync(
            id,
            new AcknowledgeRejectionDto { NewStatus = "Acknowledged", Notes = "Reviewed — safe to ignore" },
            userId);

        result.Success.Should().BeTrue();
        result.Data!.Status.Should().Be("Acknowledged");
        result.Data.AcknowledgedByUserName.Should().Be("Jane Doe");
        result.Data.AcknowledgementNotes.Should().Be("Reviewed — safe to ignore");
    }

    [Fact]
    public async Task AcknowledgeRejectionAsync_WriteOff_TransitionsToWrittenOff()
    {
        var id = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var rejection = new SyncRejectionLog
        {
            Id = id,
            RegisterId = Guid.NewGuid(),
            BranchId = Guid.NewGuid(),
            ReceiptNumber = "ACC-R1-000004",
            RejectionReason = "HashMismatch",
            Status = "Pending"
        };

        var rejectionRepo = new Mock<IRepository<SyncRejectionLog>>();
        rejectionRepo.Setup(r => r.GetQueryable())
            .Returns(AsyncQueryable(new[] { rejection }));
        _unitOfWork.Setup(u => u.Repository<SyncRejectionLog>()).Returns(rejectionRepo.Object);

        var userRepo = new Mock<IRepository<User>>();
        userRepo.Setup(r => r.GetByIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new User { Id = userId, FullName = "Kwame Admin" });
        _unitOfWork.Setup(u => u.Repository<User>()).Returns(userRepo.Object);
        _unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var sut = CreateSut();
        var result = await sut.AcknowledgeRejectionAsync(
            id,
            new AcknowledgeRejectionDto { NewStatus = "WrittenOff" },
            userId);

        result.Success.Should().BeTrue();
        result.Data!.Status.Should().Be("WrittenOff");
    }

    // ── ResolveGapAlertAsync ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ResolveGapAlertAsync_WhenGapNotFound_Returns404()
    {
        var gapRepo = new Mock<IRepository<SequenceGapAlert>>();
        gapRepo.Setup(r => r.GetQueryable())
            .Returns(AsyncQueryable(Enumerable.Empty<SequenceGapAlert>()));
        _unitOfWork.Setup(u => u.Repository<SequenceGapAlert>()).Returns(gapRepo.Object);

        var sut = CreateSut();
        var result = await sut.ResolveGapAlertAsync(
            Guid.NewGuid(),
            new ResolveGapAlertDto { ResolutionNotes = "Investigated" },
            Guid.NewGuid());

        result.Success.Should().BeFalse();
        result.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task ResolveGapAlertAsync_WhenAlreadyResolved_Returns409()
    {
        var id = Guid.NewGuid();
        var gap = new SequenceGapAlert
        {
            Id = id,
            RegisterId = Guid.NewGuid(),
            BranchId = Guid.NewGuid(),
            GapStartSequence = 10,
            GapEndSequence = 12,
            GapSize = 3,
            Status = "Resolved"
        };

        var gapRepo = new Mock<IRepository<SequenceGapAlert>>();
        gapRepo.Setup(r => r.GetQueryable())
            .Returns(AsyncQueryable(new[] { gap }));
        _unitOfWork.Setup(u => u.Repository<SequenceGapAlert>()).Returns(gapRepo.Object);

        var sut = CreateSut();
        var result = await sut.ResolveGapAlertAsync(
            id,
            new ResolveGapAlertDto { ResolutionNotes = "Already done" },
            Guid.NewGuid());

        result.Success.Should().BeFalse();
        result.StatusCode.Should().Be(409);
        result.ErrorCode.Should().Be("INVALID_STATE_TRANSITION");
    }

    [Fact]
    public async Task ResolveGapAlertAsync_WhenOpenGap_SetsResolvedStatusAndNotes()
    {
        var id = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var gap = new SequenceGapAlert
        {
            Id = id,
            RegisterId = Guid.NewGuid(),
            BranchId = Guid.NewGuid(),
            GapStartSequence = 5,
            GapEndSequence = 7,
            GapSize = 3,
            Status = "Open"
        };

        var gapRepo = new Mock<IRepository<SequenceGapAlert>>();
        gapRepo.Setup(r => r.GetQueryable())
            .Returns(AsyncQueryable(new[] { gap }));
        _unitOfWork.Setup(u => u.Repository<SequenceGapAlert>()).Returns(gapRepo.Object);

        var userRepo = new Mock<IRepository<User>>();
        userRepo.Setup(r => r.GetByIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new User { Id = userId, FullName = "Ama Manager" });
        _unitOfWork.Setup(u => u.Repository<User>()).Returns(userRepo.Object);
        _unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var sut = CreateSut();
        var result = await sut.ResolveGapAlertAsync(
            id,
            new ResolveGapAlertDto { ResolutionNotes = "Confirmed offline sale void during network outage" },
            userId);

        result.Success.Should().BeTrue();
        result.Data!.Status.Should().Be("Resolved");
        result.Data.ResolutionNotes.Should().Be("Confirmed offline sale void during network outage");
        result.Data.ResolvedByUserName.Should().Be("Ama Manager");
    }

    // ── Health Status Classification ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(2, "Healthy")]
    [InlineData(5, "Healthy")]
    [InlineData(6, "Warning")]
    [InlineData(60, "Warning")]
    [InlineData(61, "Critical")]
    [InlineData(1440, "Critical")]
    public async Task GetSyncHealthAsync_HealthStatusBasedOnMinutesSinceLastSync(
        int minutesAgo, string expectedStatus)
    {
        var registerId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var register = new Register
        {
            Id = registerId,
            RegisterName = "Register 1",
            RegisterCode = "R1",
            LocationId = branchId,
            IsActive = true,
            Location = new Branch { Id = branchId, BranchName = "Main Branch", BranchCode = "MB" }
        };

        var syncState = new RegisterSyncState
        {
            RegisterId = registerId,
            BranchId = branchId,
            LastSuccessfulSyncAt = DateTime.UtcNow.AddMinutes(-minutesAgo),
            LastAcceptedSequence = 10,
            TotalRejections = 0
        };

        var registerRepo = new Mock<IRepository<Register>>();
        registerRepo.Setup(r => r.GetQueryable())
            .Returns(AsyncQueryable(new[] { register }));
        _unitOfWork.Setup(u => u.Registers).Returns(registerRepo.Object);

        var syncStateRepo = new Mock<IRepository<RegisterSyncState>>();
        syncStateRepo.Setup(r => r.GetQueryable())
            .Returns(AsyncQueryable(new[] { syncState }));
        _unitOfWork.Setup(u => u.Repository<RegisterSyncState>()).Returns(syncStateRepo.Object);

        var rejectionRepo = new Mock<IRepository<SyncRejectionLog>>();
        rejectionRepo.Setup(r => r.GetQueryable())
            .Returns(AsyncQueryable(Enumerable.Empty<SyncRejectionLog>()));
        _unitOfWork.Setup(u => u.Repository<SyncRejectionLog>()).Returns(rejectionRepo.Object);

        var gapRepo = new Mock<IRepository<SequenceGapAlert>>();
        gapRepo.Setup(r => r.GetQueryable())
            .Returns(AsyncQueryable(Enumerable.Empty<SequenceGapAlert>()));
        _unitOfWork.Setup(u => u.Repository<SequenceGapAlert>()).Returns(gapRepo.Object);

        var sut = CreateSut();
        var result = await sut.GetSyncHealthAsync(branchId: null);

        result.Success.Should().BeTrue();
        result.Data.Should().HaveCount(1);
        result.Data![0].HealthStatus.Should().Be(expectedStatus);
        result.Data[0].MinutesSinceLastSync.Should().BeInRange(minutesAgo - 1, minutesAgo + 1);
    }

    [Fact]
    public async Task GetSyncHealthAsync_WhenNeverSynced_ReturnsUnknownStatus()
    {
        var registerId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var register = new Register
        {
            Id = registerId,
            RegisterName = "Register 1",
            RegisterCode = "R1",
            LocationId = branchId,
            IsActive = true,
            Location = new Branch { Id = branchId, BranchName = "Main Branch", BranchCode = "MB" }
        };

        var registerRepo = new Mock<IRepository<Register>>();
        registerRepo.Setup(r => r.GetQueryable())
            .Returns(AsyncQueryable(new[] { register }));
        _unitOfWork.Setup(u => u.Registers).Returns(registerRepo.Object);

        var syncStateRepo = new Mock<IRepository<RegisterSyncState>>();
        syncStateRepo.Setup(r => r.GetQueryable())
            .Returns(AsyncQueryable(Enumerable.Empty<RegisterSyncState>()));
        _unitOfWork.Setup(u => u.Repository<RegisterSyncState>()).Returns(syncStateRepo.Object);

        var rejectionRepo = new Mock<IRepository<SyncRejectionLog>>();
        rejectionRepo.Setup(r => r.GetQueryable())
            .Returns(AsyncQueryable(Enumerable.Empty<SyncRejectionLog>()));
        _unitOfWork.Setup(u => u.Repository<SyncRejectionLog>()).Returns(rejectionRepo.Object);

        var gapRepo = new Mock<IRepository<SequenceGapAlert>>();
        gapRepo.Setup(r => r.GetQueryable())
            .Returns(AsyncQueryable(Enumerable.Empty<SequenceGapAlert>()));
        _unitOfWork.Setup(u => u.Repository<SequenceGapAlert>()).Returns(gapRepo.Object);

        var sut = CreateSut();
        var result = await sut.GetSyncHealthAsync(branchId: null);

        result.Success.Should().BeTrue();
        result.Data![0].HealthStatus.Should().Be("Unknown");
        result.Data[0].LastSuccessfulSyncAt.Should().BeNull();
        result.Data[0].MinutesSinceLastSync.Should().BeNull();
    }

    [Fact]
    public async Task GetSyncHealthAsync_WhenNoRegisters_ReturnsEmptyList()
    {
        var registerRepo = new Mock<IRepository<Register>>();
        registerRepo.Setup(r => r.GetQueryable())
            .Returns(AsyncQueryable(Enumerable.Empty<Register>()));
        _unitOfWork.Setup(u => u.Registers).Returns(registerRepo.Object);

        var sut = CreateSut();
        var result = await sut.GetSyncHealthAsync(branchId: null);

        result.Success.Should().BeTrue();
        result.Data.Should().BeEmpty();
    }
}

// ── Async IQueryable infrastructure ──────────────────────────────────────────────────────

internal sealed class TestAsyncEnumerable<T> : EnumerableQuery<T>, IAsyncEnumerable<T>, IQueryable<T>
{
    public TestAsyncEnumerable(IEnumerable<T> enumerable) : base(enumerable) { }
    public TestAsyncEnumerable(Expression expression) : base(expression) { }

    IQueryProvider IQueryable.Provider => new TestAsyncQueryProvider<T>(this);

    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        => new TestAsyncEnumerator<T>(this.AsEnumerable().GetEnumerator());
}

internal sealed class TestAsyncEnumerator<T> : IAsyncEnumerator<T>
{
    private readonly IEnumerator<T> _inner;
    public TestAsyncEnumerator(IEnumerator<T> inner) => _inner = inner;
    public T Current => _inner.Current;
    public ValueTask DisposeAsync() { _inner.Dispose(); return ValueTask.CompletedTask; }
    public ValueTask<bool> MoveNextAsync() => new ValueTask<bool>(_inner.MoveNext());
}

internal sealed class TestAsyncQueryProvider<T> : IQueryProvider, IAsyncQueryProvider
{
    private readonly IQueryProvider _inner;

    internal TestAsyncQueryProvider(IQueryProvider inner) => _inner = inner;

    public IQueryable CreateQuery(Expression expression)
        => new TestAsyncEnumerable<T>(expression);

    public IQueryable<TElement> CreateQuery<TElement>(Expression expression)
        => new TestAsyncEnumerable<TElement>(expression);

    public object? Execute(Expression expression) => _inner.Execute(expression);

    public TResult Execute<TResult>(Expression expression) => _inner.Execute<TResult>(expression);

    // Supports FirstOrDefaultAsync, SingleOrDefaultAsync, AnyAsync, CountAsync etc.
    public TResult ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken = default)
    {
        var resultType = typeof(TResult).GetGenericArguments()[0];
        var syncResult = typeof(IQueryProvider)
            .GetMethod(nameof(IQueryProvider.Execute), 1, new[] { typeof(Expression) })!
            .MakeGenericMethod(resultType)
            .Invoke(_inner, new object[] { expression });

        return (TResult)typeof(Task)
            .GetMethod(nameof(Task.FromResult))!
            .MakeGenericMethod(resultType)
            .Invoke(null, new[] { syncResult })!;
    }
}
