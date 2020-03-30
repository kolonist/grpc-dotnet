using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Lb.V1;
using Grpc.Net.Client.LoadBalancing.Policies;
using Grpc.Net.Client.LoadBalancing.Policies.Abstraction;
using Moq;
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Grpc.Net.Client.LoadBalancing.Tests.Policies
{
    public sealed class GrpclbPolicyTests
    {
        [Fact]
        public async Task ForEmptyServiceName_UseGrpclbPolicy_ThrowArgumentException()
        {
            // Arrange
            using var policy = new GrpclbPolicy();
            var resolutionResults = new List<GrpcNameResolutionResult>()
            {
                new GrpcNameResolutionResult("10.1.6.120", 80)
                {
                    IsLoadBalancer = true
                },
                new GrpcNameResolutionResult("10.1.6.121", 80)
                {
                    IsLoadBalancer = true
                }
            };

            // Act
            // Assert
            var exception = await Assert.ThrowsAsync<ArgumentException>(async () =>
            {
                await policy.CreateSubChannelsAsync(resolutionResults, "", false);
            });
            Assert.Equal("serviceName not defined", exception.Message);
            exception = await Assert.ThrowsAsync<ArgumentException>(async () =>
            {
                await policy.CreateSubChannelsAsync(resolutionResults, string.Empty, false);
            });
            Assert.Equal("serviceName not defined", exception.Message);
        }

        [Fact]
        public async Task ForEmptyResolutionPassed_UseGrpclbPolicy_ThrowArgumentException()
        {
            // Arrange
            using var policy = new GrpclbPolicy();

            // Act
            // Assert
            var exception = await Assert.ThrowsAsync<ArgumentException>(async () =>
            {
                await policy.CreateSubChannelsAsync(new List<GrpcNameResolutionResult>(), "sample-service.contoso.com", false);
            });
            Assert.Equal("resolutionResult must contain at least one blancer address", exception.Message);
        }

        [Fact]
        public async Task ForServersResolutionOnly_UseGrpclbPolicy_ThrowArgumentException()
        {
            // Arrange
            using var policy = new GrpclbPolicy();
            var resolutionResults = new List<GrpcNameResolutionResult>()
            {
                new GrpcNameResolutionResult("10.1.5.211", 80)
                {
                    IsLoadBalancer = false
                },
                new GrpcNameResolutionResult("10.1.5.212", 80)
                {
                    IsLoadBalancer = false
                }
            };

            // Act
            // Assert
            var exception = await Assert.ThrowsAsync<ArgumentException>(async () =>
            {
                await policy.CreateSubChannelsAsync(resolutionResults, "sample-service.contoso.com", false); // non-balancers are ignored
            });
            Assert.Equal("resolutionResult must contain at least one blancer address", exception.Message);
        }

        [Fact]
        public async Task ForResolutionResultWithBalancers_UseGrpclbPolicy_CreateSubchannelsForFoundServers()
        {
            // Arrange
            var balancerClientMock = new Mock<ILoadBalancerClient>(MockBehavior.Strict);
            var balancerStreamMock = new Mock<IAsyncDuplexStreamingCall<LoadBalanceRequest, LoadBalanceResponse>>(MockBehavior.Strict);
            var requestStreamMock = new Mock<IClientStreamWriter<LoadBalanceRequest>>(MockBehavior.Loose);

            balancerClientMock.Setup(x => x.Dispose());
            balancerClientMock.Setup(x => x.BalanceLoad(null, null, It.IsAny<CancellationToken>()))
                .Returns(balancerStreamMock.Object);

            balancerStreamMock.Setup(x => x.RequestStream).Returns(requestStreamMock.Object);
            balancerStreamMock.Setup(x => x.ResponseStream).Returns(new TestLoadBalancerResponse(new List<LoadBalanceResponse>
            {
                new LoadBalanceResponse()
                {
                    InitialResponse = GetSampleInitialLoadBalanceResponse()
                },
                new LoadBalanceResponse()
                {
                    ServerList = GetSampleServerList()
                }
            }));

            using var policy = new GrpclbPolicy();
            policy.OverrideLoadBalancerClient = balancerClientMock.Object;

            var resolutionResults = new List<GrpcNameResolutionResult>()
            {
                new GrpcNameResolutionResult("10.1.6.120", 80) { IsLoadBalancer = true }
            };

            // Act
            await policy.CreateSubChannelsAsync(resolutionResults, "sample-service.contoso.com", false);
            var subChannels = policy.SubChannels;

            // Assert
            Assert.Equal(3, subChannels.Count); // subChannels are created per results from GetSampleLoadBalanceResponse
            Assert.All(subChannels, subChannel => Assert.Equal("http", subChannel.Address.Scheme));
            Assert.All(subChannels, subChannel => Assert.Equal(80, subChannel.Address.Port));
            Assert.All(subChannels, subChannel => Assert.StartsWith("10.1.5.", subChannel.Address.Host));
        }

        [Fact]
        public async Task ForLoadBalancerClient_UseGrpclbPolicy_EnsureDisposedResources()
        {
            // Arrange
            var balancerClientMock = new Mock<ILoadBalancerClient>(MockBehavior.Strict);
            var balancerStreamMock = new Mock<IAsyncDuplexStreamingCall<LoadBalanceRequest, LoadBalanceResponse>>(MockBehavior.Strict);
            var requestStreamMock = new Mock<IClientStreamWriter<LoadBalanceRequest>>(MockBehavior.Loose);

            balancerClientMock.Setup(x => x.Dispose()).Verifiable();
            balancerClientMock.Setup(x => x.BalanceLoad(null, null, It.IsAny<CancellationToken>()))
                .Returns(balancerStreamMock.Object);

            balancerStreamMock.Setup(x => x.RequestStream).Returns(requestStreamMock.Object);
            balancerStreamMock.Setup(x => x.ResponseStream).Returns(new TestLoadBalancerResponse(new List<LoadBalanceResponse>
            {
                new LoadBalanceResponse()
                {
                    InitialResponse = GetSampleInitialLoadBalanceResponse()
                },
                new LoadBalanceResponse()
                {
                    ServerList = GetSampleServerList()
                }
            }));

            using var policy = new GrpclbPolicy();
            policy.OverrideLoadBalancerClient = balancerClientMock.Object;

            var resolutionResults = new List<GrpcNameResolutionResult>()
            {
                new GrpcNameResolutionResult("10.1.6.120", 80) { IsLoadBalancer = true }
            };

            // Act
            await policy.CreateSubChannelsAsync(resolutionResults, "sample-service.contoso.com", false);
            var subChannels = policy.SubChannels;

            // Assert
            policy.Dispose();
            balancerClientMock.Verify(x => x.Dispose(), Times.Once());
        }

        [Fact]
        public void ForGrpcSubChannels_UseGrpclbPolicySelectChannels_SelectChannelsInRoundRobin()
        {
            // Arrange
            using var policy = new GrpclbPolicy();
            var subChannels = new List<GrpcSubChannel>()
            {
                new GrpcSubChannel(new UriBuilder("http://10.1.5.210:80").Uri),
                new GrpcSubChannel(new UriBuilder("http://10.1.5.212:80").Uri),
                new GrpcSubChannel(new UriBuilder("http://10.1.5.211:80").Uri),
                new GrpcSubChannel(new UriBuilder("http://10.1.5.213:80").Uri)
            };
            policy.SubChannels = subChannels;

            // Act
            // Assert
            for (int i = 0; i < 30; i++)
            {
                var subChannel = policy.GetNextSubChannel();
                Assert.Equal(subChannels[i % subChannels.Count].Address.Host, subChannel.Address.Host);
                Assert.Equal(subChannels[i % subChannels.Count].Address.Port, subChannel.Address.Port);
                Assert.Equal(subChannels[i % subChannels.Count].Address.Scheme, subChannel.Address.Scheme);
            }
        }

        private static InitialLoadBalanceResponse GetSampleInitialLoadBalanceResponse()
        {
            var initialResponse = new InitialLoadBalanceResponse();
            initialResponse.ClientStatsReportInterval = Duration.FromTimeSpan(TimeSpan.FromSeconds(10));
            initialResponse.LoadBalancerDelegate = string.Empty;
            return initialResponse;
        }

        private static ServerList GetSampleServerList()
        {
            var serverList = new ServerList();
            serverList.Servers.Add(new Server()
            {
                IpAddress = ByteString.CopyFrom(IPAddress.Parse("10.1.5.211").GetAddressBytes()),
                Port = 80
            });
            serverList.Servers.Add(new Server()
            {
                IpAddress = ByteString.CopyFrom(IPAddress.Parse("10.1.5.212").GetAddressBytes()),
                Port = 80
            });
            serverList.Servers.Add(new Server()
            {
                IpAddress = ByteString.CopyFrom(IPAddress.Parse("10.1.5.213").GetAddressBytes()),
                Port = 80
            });
            return serverList;
        }
    }

    internal sealed class TestLoadBalancerResponse : IAsyncStreamReader<LoadBalanceResponse>
    {
        private readonly IReadOnlyList<LoadBalanceResponse> _loadBalanceResponses;
        private int _streamIndex;

        public TestLoadBalancerResponse(IReadOnlyList<LoadBalanceResponse> loadBalanceResponses)
        {
            _loadBalanceResponses = loadBalanceResponses;
            _streamIndex = -1;
        }

        public LoadBalanceResponse Current => _loadBalanceResponses[_streamIndex];

        public Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            return Task.FromResult(++_streamIndex < _loadBalanceResponses.Count);
        }
    }
}