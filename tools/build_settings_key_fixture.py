"""Generate a candidate RSA PKCS#1 v1.5 settings response from public parameters."""
import argparse
import base64
import json
from pathlib import Path
import secrets


def encrypt(message, modulus, exponent):
    size = (modulus.bit_length() + 7) // 8
    if modulus <= 0 or exponent < 3 or exponent % 2 == 0 or exponent >= modulus:
        raise ValueError('Invalid public parameters')
    if len(message) > size - 11:
        raise ValueError('Message too long for PKCS#1 v1.5')
    padding = bytes(secrets.randbelow(255) + 1 for _ in range(size - len(message) - 3))
    block = b'\x00\x02' + padding + b'\x00' + message
    return pow(int.from_bytes(block, 'big'), exponent, modulus).to_bytes(size, 'big')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--public', type=Path, required=True)
    parser.add_argument('--output-dir', type=Path, required=True)
    args = parser.parse_args()
    public = json.loads(args.public.read_text(encoding='utf-8'))
    # ASCII avoids currently unverified non-ASCII client conversion behavior.
    key = secrets.token_hex(8)
    ciphertext = encrypt(key.encode('ascii'), int(public['modulus_hex'], 16), public['exponent'])
    args.output_dir.mkdir(parents=True, exist_ok=False)
    (args.output_dir / 'app-key.json').write_text(json.dumps({'value': base64.b64encode(ciphertext).decode('ascii')}) + '\n')
    (args.output_dir / 'local-test-key.json').write_text(json.dumps({'key': key, 'status': 'candidate_not_client_validated'}) + '\n')
    print('Candidate fixture written; client acceptance is unverified.')


if __name__ == '__main__':
    main()
