﻿using System;
using System.Threading.Tasks;
using GreenPipes;
using MassTransit.Logging;
using MassTransit.Saga;
using MassTransit.Util;
using StackExchange.Redis;

namespace MassTransit.RedisSagas
{
    public class RedisSagaRepository<TSaga> : ISagaRepository<TSaga>,
        IRetrieveSagaFromRepository<TSaga>
        where TSaga : class, IVersionedSaga
    {
        static readonly ILog _log = Logger.Get<RedisSagaRepository<TSaga>>();
        readonly Func<IDatabase> _redisDbFactory;
        private readonly string _redisPrefix;

        public RedisSagaRepository(Func<IDatabase> redisDbFactory, string redisPrefix)
        {
            _redisPrefix = redisPrefix;
            _redisDbFactory = redisDbFactory;
        }

        public RedisSagaRepository(Func<IDatabase> redisDbFactory) => _redisDbFactory = redisDbFactory;

        public async Task<TSaga> GetSaga(Guid correlationId) =>
            await _redisDbFactory().As<TSaga>().Get(correlationId, _redisPrefix).ConfigureAwait(false);

        public async Task Send<T>(ConsumeContext<T> context, ISagaPolicy<TSaga, T> policy,
            IPipe<SagaConsumeContext<TSaga, T>> next) where T : class
        {
            if (!context.CorrelationId.HasValue)
                throw new SagaException("The CorrelationId was not specified", typeof(TSaga), typeof(T));

            var sagaId = context.CorrelationId.Value;
            var db = _redisDbFactory();
            TSaga instance;
            ITypedDatabase<TSaga> sagas = db.As<TSaga>();

            if (policy.PreInsertInstance(context, out instance))
                await PreInsertSagaInstance<T>(sagas, instance).ConfigureAwait(false);

            if (instance == null)
                instance = await sagas.Get(sagaId, _redisPrefix).ConfigureAwait(false);


            if (instance == null)
            {
                var missingSagaPipe = new MissingPipe<T>(db, next);
                await policy.Missing(context, missingSagaPipe).ConfigureAwait(false);
            }
            else
            {
                await SendToInstance(context, policy, next, instance).ConfigureAwait(false);
            }
        }

        public Task SendQuery<T>(SagaQueryConsumeContext<TSaga, T> context, ISagaPolicy<TSaga, T> policy,
            IPipe<SagaConsumeContext<TSaga, T>> next) where T : class
        {
            throw new NotImplementedByDesignException("Redis saga repository does not support queries");
        }

        public void Probe(ProbeContext context)
        {
            var scope = context.CreateScope("sagaRepository");
            scope.Set(new
            {
                Persistence = "redis"
            });
        }

        async Task SendToInstance<T>(ConsumeContext<T> context, ISagaPolicy<TSaga, T> policy,
            IPipe<SagaConsumeContext<TSaga, T>> next, TSaga instance)
            where T : class
        {
            try
            {
                if (_log.IsDebugEnabled)
                    _log.DebugFormat("SAGA:{0}:{1} Used {2}", TypeMetadataCache<TSaga>.ShortName, instance.CorrelationId, TypeMetadataCache<T>.ShortName);

                var sagaConsumeContext = new RedisSagaConsumeContext<TSaga, T>(_redisDbFactory(), context, instance);

                await policy.Existing(sagaConsumeContext, next).ConfigureAwait(false);

                if (!sagaConsumeContext.IsCompleted)
                    await UpdateRedisSaga(instance).ConfigureAwait(false);
            }
            catch (SagaException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new SagaException(ex.Message, typeof(TSaga), typeof(T), instance.CorrelationId, ex);
            }
        }

        async Task<bool> PreInsertSagaInstance<T>(ITypedDatabase<TSaga> sagas, TSaga instance)
        {
            try
            {
                await sagas.Put(instance.CorrelationId, instance, _redisPrefix).ConfigureAwait(false);

                if (_log.IsDebugEnabled)
                    _log.DebugFormat("SAGA:{0}:{1} Insert {2}", TypeMetadataCache<TSaga>.ShortName, instance.CorrelationId,
                        TypeMetadataCache<T>.ShortName);
                return true;
            }
            catch (Exception ex)
            {
                if (_log.IsDebugEnabled)
                    _log.DebugFormat("SAGA:{0}:{1} Dupe {2} - {3}", TypeMetadataCache<TSaga>.ShortName,
                        instance.CorrelationId,
                        TypeMetadataCache<T>.ShortName, ex.Message);
                return false;
            }
        }

        async Task UpdateRedisSaga(TSaga instance)
        {
            ITypedDatabase<TSaga> sagas = _redisDbFactory().As<TSaga>();

            instance.Version++;
            var old = await sagas.Get(instance.CorrelationId, _redisPrefix).ConfigureAwait(false);
            if (old.Version > instance.Version)
                throw new RedisSagaConcurrencyException($"Version conflict for saga with id {instance.CorrelationId}");

            await sagas.Put(instance.CorrelationId, instance, _redisPrefix).ConfigureAwait(false);
        }


        /// <summary>
        ///     Once the message pipe has processed the saga instance, add it to the saga repository
        /// </summary>
        /// <typeparam name="TMessage"></typeparam>
        class MissingPipe<TMessage> :
            IPipe<SagaConsumeContext<TSaga, TMessage>>
            where TMessage : class
        {
            readonly IPipe<SagaConsumeContext<TSaga, TMessage>> _next;
            readonly IDatabase _redisDb;
            private readonly string _redisPrefix;

            public MissingPipe(IDatabase redisDb, IPipe<SagaConsumeContext<TSaga, TMessage>> next, string redisPrefix = "")
            {
                _redisDb = redisDb;
                _next = next;
                _redisPrefix = redisPrefix;
            }

            void IProbeSite.Probe(ProbeContext context)
            {
                _next.Probe(context);
            }

            public async Task Send(SagaConsumeContext<TSaga, TMessage> context)
            {
                if (_log.IsDebugEnabled)
                    _log.DebugFormat("SAGA:{0}:{1} Added {2}", TypeMetadataCache<TSaga>.ShortName,
                        context.Saga.CorrelationId,
                        TypeMetadataCache<TMessage>.ShortName);

                SagaConsumeContext<TSaga, TMessage> proxy = new RedisSagaConsumeContext<TSaga, TMessage>(_redisDb,
                    context, context.Saga);

                await _next.Send(proxy).ConfigureAwait(false);

                if (!proxy.IsCompleted)
                    await _redisDb.As<TSaga>().Put(context.Saga.CorrelationId, context.Saga, _redisPrefix).ConfigureAwait(false);
            }
        }
    }
}