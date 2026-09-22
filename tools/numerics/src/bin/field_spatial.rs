fn main() {
    let result = (|| {
        let p = continuum_numerics::output_argument()?;
        continuum_numerics::write_json_exclusive(&p, &continuum_numerics::field::spatial_report())
    })();
    if let Err(e) = result {
        eprintln!("field-spatial: {e}");
        std::process::exit(1)
    }
}
