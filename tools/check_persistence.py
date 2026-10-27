"""Run built C# repository checks against a fresh disposable PostgreSQL database.

Uses the non-superuser application role for all repository operations. Admin
credentials are used only to create/migrate/seed/drop the isolated test database.
"""
import json
import os
import subprocess
import uuid

try:
    from .postgres_dev import ROOT, HOME, PORT, sql, migration_script
except ImportError:
    from postgres_dev import ROOT, HOME, PORT, sql, migration_script


def main():
    assembly = ROOT / 'tests/Aion.Persistence.Checks/bin/Debug/net10.0/Aion.Persistence.Checks.dll'
    if os.environ.get('AION_ARTIFACTS_PATH'):
        assembly = ROOT / os.environ['AION_ARTIFACTS_PATH'] / 'bin/Aion.Persistence.Checks/debug/Aion.Persistence.Checks.dll'
    if not assembly.exists():
        raise ValueError('Build tests/Aion.Persistence.Checks/Aion.Persistence.Checks.csproj first')
    name = 'aion_repository_test_' + uuid.uuid4().hex
    accounts = [str(uuid.uuid4()) for _ in range(3)]
    disabled_character = str(uuid.uuid4())
    credentials = json.loads((HOME / 'credentials.json').read_text())
    sql(f'CREATE DATABASE {name} OWNER aion_owner;', database='postgres')
    try:
        sql(f'REVOKE ALL ON DATABASE {name} FROM PUBLIC; GRANT CONNECT ON DATABASE {name} TO aion_app;', database=name)
        sql(migration_script(sorted((ROOT / 'database/migrations').glob('*.sql'))), database=name)
        for account in accounts:
            sql(f"INSERT INTO auth.accounts(account_id,provider,subject) VALUES('{account}','repository-test','{account}');", database=name)
        sql(f"INSERT INTO game.characters(character_id,account_id,world_id,name,creation_id,race,gender) VALUES('{disabled_character}','{accounts[2]}',1204,'PreviouslyOwned',1,1,1);", database=name)
        sql(f"UPDATE auth.accounts SET disabled_at=now() WHERE account_id='{accounts[2]}';", database=name)
        sql(f"INSERT INTO auth.account_roles VALUES('{accounts[1]}','gamemaster');", database=name)
        sql("""
            INSERT INTO catalog.builds VALUES('test','{}',now());
            INSERT INTO catalog.items VALUES('test',101,'Sword','weapon','{}'),('test',102,'Unknown','unknown','{}');
            INSERT INTO catalog.skills VALUES('test',201,'Strike','skill','{}'),('test',202,'Slash','skill','{}'),('test',203,'Whirl','skill','{}'),('test',204,'Shield','skill','{}');
            INSERT INTO game.skill_unlocks VALUES('test',1,201,1,1),('test',1,202,5,1),('test',1,203,12,1),('test',2,201,1,1),('test',2,204,1,1);
            INSERT INTO game.equipment_rules VALUES('test',101,1,1,ARRAY[1],'synthetic test policy');
            INSERT INTO game.starter_profiles VALUES('test','test',1,1,1,true,
              '{"worldId":1204,"mapId":1000,"position":{"x":1,"y":2,"z":3},"resources":{"hp":1500,"mp":200},"skills":[201],"slots":[[0,201]],"items":[{"templateId":101,"quantity":1,"equipmentSlot":1}],"characterClass":"Gladiator"}');
            INSERT INTO game.starter_profiles SELECT 'broken',build_id,creation_id,race,gender,true,
              jsonb_set(definition,'{skills}','[999999]') FROM game.starter_profiles WHERE profile_id='test';
            -- Class change targets: another class of the same race, and one of the other race.
            INSERT INTO game.starter_profiles SELECT 'templar',build_id,2,race,gender,true,
              jsonb_set(definition,'{characterClass}','"Templar"') FROM game.starter_profiles WHERE profile_id='test';
            INSERT INTO game.starter_profiles SELECT 'other-race',build_id,9,2,gender,true,definition FROM game.starter_profiles WHERE profile_id='test';
            """, database=name)
        # Passwords are generated URL-safe strings; they are passed only via child env.
        env = os.environ.copy()
        env.update(AION_TEST_DATABASE=f'Host=127.0.0.1;Port={PORT};Database={name};Username=aion_app;Password={credentials["aion_app"]};Maximum Pool Size=4;Timeout=5;Command Timeout=10',
                   AION_TEST_FIRST=accounts[0], AION_TEST_SECOND=accounts[1], AION_TEST_DISABLED=accounts[2],
                   AION_TEST_DISABLED_CHARACTER=disabled_character)
        result = subprocess.run(['dotnet', str(assembly)], env=env, cwd=ROOT,
                                capture_output=True, text=True, encoding='utf-8', timeout=90)
        output = result.stdout + result.stderr
        for password in credentials.values():
            output = output.replace(password, '[redacted]')
        print(output.strip())
        if result.returncode:
            raise RuntimeError('Repository checks failed')
    finally:
        # Generated name only; never target the development aion database.
        sql(f'DROP DATABASE {name} WITH (FORCE);', database='postgres')
    print('Disposable test database removed; development data unchanged.')


if __name__ == '__main__':
    main()
