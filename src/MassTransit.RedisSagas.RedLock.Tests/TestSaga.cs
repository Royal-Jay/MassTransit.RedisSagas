﻿using System;
using MassTransit.Saga;

namespace MassTransit.RedisSagas.RedLock.Tests
{
    public class TestSaga : ISaga
    {
        public Guid Id => CorrelationId;
        public Guid CorrelationId { get; set; }
    }
}
