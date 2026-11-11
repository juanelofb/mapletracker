"""Fine-tune an unchanged-character-set Tesseract model with native training executables."""
import argparse
import json
import os
import random
import shutil
import struct
import subprocess
from pathlib import Path

from dataset import digest, transcript, write_json


def run(command, log, environment):
    log.write('COMMAND ' + json.dumps([str(c) for c in command]) + '\n')
    log.flush()
    result = subprocess.run([str(c) for c in command], stdout=log, stderr=subprocess.STDOUT,
                            env=environment, check=False)
    log.flush()
    if result.returncode:
        raise RuntimeError(f'{Path(command[0]).name} failed with exit {result.returncode}; inspect training.log.')


def prepare(source, destination, tesseract, tessdata, config, log, environment, language='eng'):
    destination.mkdir()
    paths = []
    for image in sorted(source.glob('sample-*.png')):
        text = transcript(image.with_suffix('.gt.txt'))
        if any(ord(character) > 127 for character in text):
            raise ValueError('This first unchanged-character-set trial is restricted to ASCII transcripts.')
        local_image = destination / image.name
        shutil.copyfile(image, local_image)
        width, height = struct.unpack('>II', image.read_bytes()[16:24])
        box = ''.join(f'{character} 0 0 {width} {height} 0\n' for character in text)
        box += f'\t 0 0 {width} {height} 0\n'
        local_image.with_suffix('.box').write_text(box, encoding='utf-8', newline='\n')
        run([tesseract, local_image, local_image.with_suffix(''), '--tessdata-dir', tessdata, '-l', language,
             '--psm', '13', config], log, environment)
        training_file = local_image.with_suffix('.lstmf')
        if not training_file.is_file():
            raise RuntimeError('Tesseract did not produce ' + str(training_file))
        paths.append(training_file)
    if not paths:
        raise ValueError('No samples found in ' + str(source))
    random.Random(42).shuffle(paths)
    listing = destination.parent / (destination.name + '.list')
    listing.write_text(''.join(path.as_posix() + '\n' for path in paths), encoding='utf-8', newline='\n')
    return listing, len(paths)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--tools', type=Path, required=True)
    parser.add_argument('--base-model', type=Path, required=True)
    parser.add_argument('--train', type=Path, required=True)
    parser.add_argument('--validation', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--iterations', type=int, default=400)
    args = parser.parse_args()
    if not 1 <= args.iterations <= 10000:
        raise ValueError('Choose a bounded iteration count between 1 and 10000.')
    tools, output, base = args.tools.resolve(), args.output.resolve(), args.base_model.resolve()
    for name in ['tesseract.exe', 'combine_tessdata.exe', 'lstmtraining.exe']:
        if not (tools / name).is_file():
            raise ValueError('Missing training executable: ' + name)
    if output.exists():
        raise ValueError('Output already exists; choose a new experiment directory.')
    if {p.stem for p in args.train.glob('sample-*.png')} & {p.stem for p in args.validation.glob('sample-*.png')}:
        raise ValueError('Training and validation sample IDs overlap.')
    output.mkdir(parents=True)
    environment = {**os.environ, 'OMP_THREAD_LIMIT': '2'}
    with (output / 'training.log').open('w', encoding='utf-8') as log:
        run([tools / 'combine_tessdata.exe', '-u', base, output / 'base.'], log, environment)
        config = tools / 'tessdata/configs/lstm.train'
        train_list, train_count = prepare(args.train.resolve(), output / 'train-lines', tools / 'tesseract.exe', base.parent, config, log, environment, base.stem)
        validation_list, validation_count = prepare(args.validation.resolve(), output / 'validation-lines', tools / 'tesseract.exe', base.parent, config, log, environment, base.stem)
        print(f'Prepared {train_count} training lines and {validation_count} validation lines.', flush=True)
        checkpoint_prefix = output / 'maple_chat'
        run([tools / 'lstmtraining.exe', '--continue_from', output / 'base.lstm',
             '--traineddata', base, '--model_output', checkpoint_prefix,
             '--train_listfile', train_list, '--eval_listfile', validation_list,
             '--learning_rate', '0.0001', '--max_iterations', str(args.iterations),
             '--max_image_MB', '256', '--target_error_rate', '0.01'], log, environment)
        checkpoint = output / 'maple_chat_checkpoint'
        model_directory = output / 'models'
        model_directory.mkdir()
        for name, quantize in [('maple_chat', False), ('maple_chat_int', True)]:
            command = [tools / 'lstmtraining.exe', '--stop_training', '--continue_from', checkpoint,
                       '--traineddata', base, '--model_output', model_directory / (name + '.traineddata')]
            if quantize:
                command.append('--convert_to_int')
            run(command, log, environment)
        write_json(output / 'run.json', dict(base_model_sha256=digest(base), iterations=args.iterations,
                   training_lines=train_count, validation_lines=validation_count, threads=2,
                   model_sha256={p.name: digest(p) for p in model_directory.glob('*.traineddata')}))
        print('Exported floating-point and integer MapleStory candidates to ' + str(model_directory), flush=True)


if __name__ == '__main__':
    main()
