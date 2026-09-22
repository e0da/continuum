fn main() {
    let result = (|| {
        let p = continuum_numerics::output_argument()?;
        let r = continuum_numerics::field::field_report();
        continuum_numerics::write_json_exclusive(&p, &r)
    })();
    if let Err(e) = result {
        eprintln!("field-gravity: {e}");
        std::process::exit(1)
    }
}
