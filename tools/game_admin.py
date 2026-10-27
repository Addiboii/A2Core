"""Local authenticated admin console. Roles must already be assigned by an operator.

Commands: /item CHARACTER BUILD TEMPLATE [QUANTITY], /level CHARACTER LEVEL,
/teleport CHARACTER MAP X Y Z, /disable ACCOUNT, /quit.
"""
import argparse
import getpass
import json
import shlex
import uuid
from urllib.request import Request, build_opener, ProxyHandler
from urllib.error import HTTPError


def parse_command(line, request_id=None):
    parts = shlex.split(line)
    if not parts:
        raise ValueError('Enter a command')
    command, *args = parts
    if command == '/disable' and len(args) == 1:
        return '/api/admin/accounts/' + str(uuid.UUID(args[0])) + '/disable', {}
    body = {'requestId': str(request_id or uuid.uuid4())}
    if not args:
        raise ValueError('A target character is required')
    body['characterId'] = str(uuid.UUID(args[0]))
    if command == '/item' and len(args) in (3, 4):
        body.update(command='grant', buildId=args[1], templateId=int(args[2]), quantity=int(args[3]) if len(args) == 4 else 1)
    elif command == '/level' and len(args) == 2:
        body.update(command='level', level=int(args[1]))
    elif command == '/teleport' and len(args) == 5:
        body.update(command='teleport', mapId=int(args[1]), x=float(args[2]), y=float(args[3]), z=float(args[4]))
    else:
        raise ValueError('Unknown command or incorrect arguments')
    return '/api/admin/commands', body


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--port', type=int, default=8780)
    args = parser.parse_args()
    opener = build_opener(ProxyHandler({}))
    token = None

    def post(path, body):
        headers = {'Content-Type': 'application/json'}
        if token:
            headers['Authorization'] = 'Bearer ' + token
        request = Request(f'http://127.0.0.1:{args.port}' + path, data=json.dumps(body, allow_nan=False).encode(), headers=headers)
        with opener.open(request, timeout=15) as response:
            data = response.read()
            return json.loads(data) if data else None

    try:
        session = post('/api/accounts/login', dict(username=input('Username: '), password=getpass.getpass()))
        token = session['token']
        print(__doc__)
        while True:
            line = input('admin> ')
            if line.strip() == '/quit':
                break
            try:
                path, body = parse_command(line)
                # Print nonsecret request ID before submission so network-uncertain
                # results can be reconciled/retried through the typed API.
                if 'requestId' in body:
                    print('Request:', body['requestId'])
                print(post(path, body) or 'Done')
            except HTTPError as error:
                print('Request rejected; HTTP', error.code)
            except ValueError as error:
                print(error)
    finally:
        if token:
            post('/api/accounts/logout', {})


if __name__ == '__main__':
    main()
