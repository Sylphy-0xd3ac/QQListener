#ifndef PMHQ_SIGN_JSON_H
#define PMHQ_SIGN_JSON_H
#include <stddef.h>
#include <stdint.h>
#include <string.h>
#include <limits.h>

typedef struct { const char *data; size_t size; } Slice;
typedef struct { int32_t code; Slice sign, token, extra; } SignReply;
typedef struct { const char *p, *end; } JsonCursor;

static void json_space(JsonCursor *j) {
    while (j->p < j->end && (*j->p == ' ' || *j->p == '\r' || *j->p == '\n' || *j->p == '\t')) ++j->p;
}
static int hex_digit(unsigned char c) {
    return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
}
static int hex_valid(Slice v) {
    if (v.size & 1) return 0;
    for (size_t i = 0; i < v.size; ++i) if (!hex_digit((unsigned char)v.data[i])) return 0;
    return 1;
}
/* Recognized keys and signature values must be unescaped ASCII. Ignored strings
 * can contain JSON escapes; no decoded secret is logged or persisted. */
static int json_string(JsonCursor *j, Slice *v, int plain) {
    if (j->p == j->end || *j->p++ != '"') return 0;
    v->data = j->p;
    while (j->p < j->end) {
        unsigned char c = (unsigned char)*j->p++;
        if (c == '"') { v->size = (size_t)(j->p - v->data - 1); return 1; }
        if (c < 32) return 0;
        if (c == '\\') {
            if (plain || j->p == j->end) return 0;
            c = (unsigned char)*j->p++;
            if (c == 'u') {
                if (j->end - j->p < 4) return 0;
                for (int i = 0; i < 4; ++i) if (!hex_digit((unsigned char)*j->p++)) return 0;
            } else if (!strchr("\"\\/bfnrt", c)) return 0;
        }
    }
    return 0;
}
static int json_number(JsonCursor *j, Slice *v) {
    v->data = j->p;
    if (j->p < j->end && *j->p == '-') ++j->p;
    if (j->p == j->end) return 0;
    if (*j->p == '0') ++j->p;
    else {
        if (*j->p < '1' || *j->p > '9') return 0;
        do { ++j->p; } while (j->p < j->end && *j->p >= '0' && *j->p <= '9');
    }
    if (j->p < j->end && *j->p == '.') {
        ++j->p;
        const char *start = j->p;
        while (j->p < j->end && *j->p >= '0' && *j->p <= '9') ++j->p;
        if (start == j->p) return 0;
    }
    if (j->p < j->end && (*j->p == 'e' || *j->p == 'E')) {
        ++j->p;
        if (j->p < j->end && (*j->p == '+' || *j->p == '-')) ++j->p;
        const char *start = j->p;
        while (j->p < j->end && *j->p >= '0' && *j->p <= '9') ++j->p;
        if (start == j->p) return 0;
    }
    v->size = (size_t)(j->p - v->data);
    return 1;
}
static int json_skip(JsonCursor *j, unsigned depth) {
    json_space(j);
    if (j->p == j->end || depth > 8) return 0;
    char c = *j->p;
    Slice v;
    if (c == '"') return json_string(j, &v, 0);
    if (c == '{' || c == '[') {
        ++j->p;
        char end = c == '{' ? '}' : ']';
        json_space(j);
        if (j->p < j->end && *j->p == end) { ++j->p; return 1; }
        while (j->p < j->end) {
            if (c == '{') {
                if (!json_string(j, &v, 0)) return 0;
                json_space(j);
                if (j->p == j->end || *j->p++ != ':') return 0;
            }
            if (!json_skip(j, depth + 1)) return 0;
            json_space(j);
            if (j->p == j->end) return 0;
            char sep = *j->p++;
            if (sep == end) return 1;
            if (sep != ',') return 0;
            json_space(j);
        }
        return 0;
    }
    const char *literal = c == 't' ? "true" : c == 'f' ? "false" : c == 'n' ? "null" : NULL;
    if (literal) {
        size_t n = strlen(literal);
        if ((size_t)(j->end - j->p) < n || memcmp(j->p, literal, n)) return 0;
        j->p += n;
        return 1;
    }
    return json_number(j, &v);
}
static int slice_equal(Slice s, const char *word) { return s.size == strlen(word) && !memcmp(s.data, word, s.size); }
static int json_int32(JsonCursor *j, int32_t *value) {
    Slice v;
    if (!json_number(j, &v)) return 0;
    int neg = v.size && *v.data == '-';
    uint64_t n = 0, limit = neg ? 2147483648ULL : 2147483647ULL;
    for (size_t i = neg; i < v.size; ++i) {
        if (v.data[i] < '0' || v.data[i] > '9') return 0;
        n = n * 10 + (unsigned)(v.data[i] - '0');
        if (n > limit) return 0;
    }
    *value = neg ? (int32_t)(-(int64_t)n) : (int32_t)n;
    return 1;
}
static int parse_sign_reply(const char *data, size_t size, SignReply *out) {
    JsonCursor j = {data, data + size};
    unsigned seen = 0;
    memset(out, 0, sizeof(*out));
    json_space(&j);
    if (j.p == j.end || *j.p++ != '{') return 0;
    json_space(&j);
    if (j.p == j.end || *j.p == '}') return 0;
    while (j.p < j.end) {
        Slice key;
        if (!json_string(&j, &key, 1)) return 0;
        json_space(&j);
        if (j.p == j.end || *j.p++ != ':') return 0;
        json_space(&j);
        unsigned bit = slice_equal(key, "code") ? 1 : slice_equal(key, "sign") ? 2 : slice_equal(key, "token") ? 4 : slice_equal(key, "extra") ? 8 : 0;
        if (bit && (seen & bit)) return 0;
        seen |= bit;
        if (bit == 1) { if (!json_int32(&j, &out->code)) return 0; }
        else if (bit) {
            Slice *v = bit == 2 ? &out->sign : bit == 4 ? &out->token : &out->extra;
            if (!json_string(&j, v, 1) || !hex_valid(*v)) return 0;
        } else if (!json_skip(&j, 0)) return 0;
        json_space(&j);
        if (j.p == j.end) return 0;
        char sep = *j.p++;
        if (sep == '}') {
            json_space(&j);
            if (j.p != j.end || !(seen & 1)) return 0;
            return out->code != 0 || (seen == 15 && out->sign.size != 0);
        }
        if (sep != ',') return 0;
        json_space(&j);
    }
    return 0;
}
#endif
