"""Prove fakeredis runs SearXNG's actual limiter Lua scripts in-memory."""
import time
import fakeredis

# Verbatim from searx/valkeylib.py
INCR_SLIDING_WINDOW = """
local expire = tonumber(ARGV[1])
local name = KEYS[1]
local current_time = redis.call('TIME')

redis.call('ZREMRANGEBYSCORE', name, 0, current_time[1] - expire)
redis.call('ZADD', name, current_time[1], current_time[1] .. current_time[2])
local result = redis.call('ZCOUNT', name, 0, current_time[1] + 1)
redis.call('EXPIRE', name, expire)
return result
"""

INCR_COUNTER = """
local limit = tonumber(ARGV[1])
local expire = tonumber(ARGV[2])
local c_name = KEYS[1]
local c = redis.call('GET', c_name)
if not c then
    c = redis.call('INCR', c_name)
    if expire > 0 then
        redis.call('EXPIRE', c_name, expire)
    end
else
    c = tonumber(c)
    if limit == 0 or c < limit then
       c = redis.call('INCR', c_name)
    end
end
return c
"""

PURGE_BY_PREFIX = """
local prefix = tostring(ARGV[1])
for i, name in ipairs(redis.call('KEYS', prefix .. '*')) do
    redis.call('EXPIRE', name, 0)
end
"""

client = fakeredis.FakeStrictRedis()

# 1) sliding window — the limiter's core
sw = client.register_script(INCR_SLIDING_WINDOW)
counts = [sw(keys=["SearXNG_counter_x"], args=[2]) for _ in range(5)]
print("sliding window counts (duration=2s):", counts)
assert counts == [1, 2, 3, 4, 5], counts

# 2) window slides after expiry
time.sleep(2.2)
after = sw(keys=["SearXNG_counter_x"], args=[2])
print("after waiting out the window:", after)
assert after == 1, after

# 3) incr_counter with hard limit of 3
ic = client.register_script(INCR_COUNTER)
capped = [ic(keys=["SearXNG_counter_y"], args=[3, 5]) for _ in range(6)]
print("counter with limit=3:", capped)
assert capped == [1, 2, 3, 3, 3, 3], capped

# 4) purge-by-prefix (uses KEYS + EXPIRE in Lua)
client.set("SearXNG_a", "1"); client.set("SearXNG_b", "1"); client.set("other", "1")
client.register_script(PURGE_BY_PREFIX)(args=["SearXNG_"])
survivors = sorted(k.decode() for k in client.keys("*"))
print("keys surviving purge:", survivors)
assert survivors == ["other"], survivors

# 5) delete (used by drop_counter)
client.delete("SearXNG_counter_y")
print("drop_counter delete OK:", client.exists("SearXNG_counter_y") == 0)

print("\nALL LIMITER SCRIPTS PASS on fakeredis — in-memory rate limiter is viable.")
