# Support channel compaction

Every subscribe request now offers the `channel_compaction` flag. When the server supports and allows it (Centrifugo PRO `allow_channel_compaction` namespace option), the subscribe reply carries a numeric channel ID and subsequent publication/join/leave pushes use that ID instead of the string channel name — the SDK routes such pushes via an internal ID registry. Servers without compaction support ignore the flag, so behavior is unchanged there (verified by the full integration suite against OSS Centrifugo).

Implementation notes:

* IDs are server-session-scoped: the registry is dropped on transport teardown and each subscription re-registers from its subscribe reply — including when the server assigns the same ID again after reconnect.
* Registration/clearing is atomic with subscription state under the subscription lock, so a subscribe reply racing an unsubscribe can never leave a stale registry entry.
* Compacted pushes with an unknown ID are dropped — they can't belong to server-side subscriptions (those never negotiate compaction).
* Tests use an in-process Kestrel fake server speaking the protobuf protocol, since compaction can't be enabled on the OSS docker-compose server.

Also fixes a pre-existing race: a subscribe reply produced by a connection that was torn down between the reply arriving and being processed could flip the subscription to `Subscribed` while the client reconnects — the post-reconnect resubscribe sweep then skipped it, stranding the subscription without a server-side counterpart. The client now tracks a connection generation (bumped under the state lock whenever it leaves `Connected`), the subscribe pipeline stamps each command with it, and `HandleSubscribeReply` discards mismatched replies and schedules a retry instead.
