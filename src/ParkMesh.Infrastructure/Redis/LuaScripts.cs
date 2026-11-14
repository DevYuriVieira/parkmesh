namespace ParkMesh.Infrastructure.Redis;

internal static class LuaScripts
{
    // KEYS[1] = lease key ("{prefix}:lease:bay:{bayId}")
    // KEYS[2] = fence key ("{prefix}:fence:bay:{bayId}")
    // ARGV[1] = vehicleSessionId
    // ARGV[2] = ttlMilliseconds
    public const string AcquireBayLease = """
        if redis.call("EXISTS", KEYS[1]) == 1 then
            return {0, 0}
        end

        local token = redis.call("INCR", KEYS[2])
        local payload = ARGV[1] .. ":" .. tostring(token)
        redis.call("SET", KEYS[1], payload, "PX", tonumber(ARGV[2]))
        return {1, token}
        """;

    // KEYS[1] = lease key ("{prefix}:lease:bay:{bayId}")
    // ARGV[1] = expectedFencingToken
    public const string ReleaseBayLease = """
        local current = redis.call("GET", KEYS[1])
        if not current then
            return 0
        end

        local expectedSuffix = ":" .. ARGV[1]
        if string.sub(current, -string.len(expectedSuffix)) == expectedSuffix then
            redis.call("DEL", KEYS[1])
            return 1
        end

        return 0
        """;
}
