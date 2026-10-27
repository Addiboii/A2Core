"""Validate a candidate lobby-account layout without retaining account/session values.

Layout reference: nuriland/a2kit game/parse.go (account), commit
fb693ba0bb190cc8e371173c650b52417ec2745b. Semantic labels remain candidates.
"""
import argparse
import json
from pathlib import Path
import uuid

try:
    from .aion_protocol import Reader, split_frames
    from .inspect_capture import inspect
except ImportError:
    from aion_protocol import Reader, split_frames
    from inspect_capture import inspect


def account_shape(body):
    if len(body) > 1024:
        raise ValueError('Account candidate exceeds size limit')
    reader = Reader(body)
    if reader.uint(2) != 0x3906 or reader.uint(2) != 0 or reader.uint(1) != 0:
        raise ValueError('Unexpected account header')

    def text():
        raw = reader.take(reader.uint(1))
        try:
            return raw.decode('utf-8')
        except UnicodeError:
            raise ValueError('Invalid text encoding') from None

    first = text()
    reader.take(3)
    combined = text()
    account, separator, second = combined.partition(':')
    if not separator or not account or not second:
        raise ValueError('Missing account/session separator or value')
    server = reader.uint(2)
    reader.take(5)
    if reader.uint(2) != server:
        raise ValueError('Repeated server field differs')
    reader.take(3)
    if reader.offset != len(body):
        raise ValueError('Trailing account bytes')

    def guid_shape(value):
        try:
            return str(uuid.UUID(value)) == value.lower()
        except ValueError:
            return False

    return {'body_bytes':len(body), 'account_text_length':len(account),
            'account_is_decimal':account.isascii() and account.isdecimal(),
            'session_text_lengths':[len(first),len(second)],
            'session_guid_shapes':[guid_shape(first),guid_shape(second)],
            'server_candidate':server,'repeated_server_matches':True,
            'fully_consumed':True}


def analyze(path):
    flows = []

    def observe(flow, direction, prefix):
        if direction != 'server_to_client':
            return
        stream = flow['directions'][direction]
        item = {'server_port':flow['server_port'], 'client_port':flow['client_port'],
                'accounts':[], 'failures':[]}
        flows.append(item)
        if any(stream.get(k) for k in ('conflicting_bytes','gap_after_prefix',
                'retention_limit_reached','prefix_limit_reached','truncated_payload_packets')):
            item['failures'].append('Stream incomplete or conflicting')
            return
        frames, consumed = split_frames(prefix)
        if consumed != len(prefix):
            item['failures'].append('Trailing incomplete frame')
            return
        for frame in frames:
            if frame.body[:2] != b'\x06\x39':
                continue
            try:
                item['accounts'].append(account_shape(frame.body))
            except ValueError:
                item['failures'].append('Account candidate layout rejected')

    inspect(path, 13700, stream_observer=observe)
    return {'source':str(path),'flows':flows,
            'limitations':['Only uncompressed 0x3906 frames inspected.',
                           'Account/session semantics are candidate labels from public research.',
                           'No account or session values retained; no client decryption or authentication implemented.']}


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('input',type=Path)
    parser.add_argument('--output',type=Path,required=True)
    args=parser.parse_args()
    local=Path('research/local').resolve()
    output=args.output.resolve()
    if not output.is_relative_to(local) or output.exists():
        parser.error('Use a new output under research/local/')
    report=analyze(args.input)
    output.parent.mkdir(parents=True,exist_ok=True)
    output.write_text(json.dumps(report,indent=2),encoding='utf-8')
    print(json.dumps(report,indent=2))


if __name__=='__main__':
    main()
