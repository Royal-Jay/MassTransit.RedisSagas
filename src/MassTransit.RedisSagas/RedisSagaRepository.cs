﻿namespace MassTransit.RedisSagas
{
    using System;
    using System.Threading.Tasks;
    using GreenPipes;
    using Saga;
    using StackExchange.Redis;
    using MassTransit.RedisSagas.Contexts;

    public class RedisSagaRepository<TSaga> :
        ISagaRepository<TSaga>,
        ILoadSagaRepository<TSaga>
        where TSaga : class, IVersionedSaga
    {
        readonly ISagaRepository<TSaga> _repository;
        readonly RedisSagaRepositoryContextFactory<TSaga> _repositoryContextFactory;

        public RedisSagaRepository(IConnectionMultiplexer redisConnection, bool optimistic = true, TimeSpan? lockTimeout = null, TimeSpan? lockRetryTimeout = null,
            string keyPrefix = "", TimeSpan? expiry = null)
        {
            var options = new RedisSagaRepositoryOptions<TSaga>(optimistic ? ConcurrencyMode.Optimistic : ConcurrencyMode.Pessimistic, lockTimeout, null,
                keyPrefix, SelectDefaultDatabase, expiry);

            var consumeContextFactory = new RedisSagaConsumeContextFactory<TSaga>();

            _repositoryContextFactory = new RedisSagaRepositoryContextFactory<TSaga>(redisConnection, consumeContextFactory, options);

            _repository = new SagaRepository<TSaga>(_repositoryContextFactory);
        }

        public Task<TSaga> Load(Guid correlationId)
        {
            return _repositoryContextFactory.Execute(context => context.Load(correlationId));
        }

        void IProbeSite.Probe(ProbeContext context)
        {
            _repository.Probe(context);
        }

        Task ISagaRepository<TSaga>.Send<T>(ConsumeContext<T> context, ISagaPolicy<TSaga, T> policy,
            IPipe<SagaConsumeContext<TSaga, T>> next)
        {
            return _repository.Send(context, policy, next);
        }

        Task ISagaRepository<TSaga>.SendQuery<T>(ConsumeContext<T> context, ISagaQuery<TSaga> query, ISagaPolicy<TSaga, T> policy,
            IPipe<SagaConsumeContext<TSaga, T>> next)
        {
            return _repository.SendQuery(context, query, policy, next);
        }

        static IDatabase SelectDefaultDatabase(IConnectionMultiplexer multiplexer)
        {
            return multiplexer.GetDatabase();
        }
    }
}
