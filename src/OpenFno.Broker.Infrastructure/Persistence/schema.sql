-- The broker's own database. Safe to run on every start.

-- Every event the broker has recorded, in order. Append-only: rows are never
-- updated or deleted, and the state is rebuilt from them on start.
create table if not exists journal (
    seq         bigint      primary key,
    type        text        not null,
    client_id   text        not null,
    occurred_at timestamptz not null,
    body        jsonb       not null
);

create index if not exists journal_client_seq on journal (client_id, seq);

-- Every API call, with where its time went. Written behind the request, so a
-- slow insert never delays a client.
create table if not exists request_log (
    id          bigint           generated always as identity primary key,
    at          timestamptz      not null,
    client_id   text,
    app_id      text,
    client_ip   text,
    method      text             not null,
    path        text             not null,
    status      integer          not null,
    error_code  text,
    order_id    text,
    total_ms    double precision not null,
    auth_ms     double precision,
    rate_ms     double precision,
    queue_ms    double precision,
    decide_ms   double precision,
    journal_ms  double precision,
    body        text
);

create index if not exists request_log_client_at on request_log (client_id, at desc);
