using Birko.Data.Attributes;

namespace Birko.Models
{
    public class DatabaseValueData : ValueData
    {
        [PrecisionField(StoreDecimalPrecision)]
        [ScaleField(StoreDecimalPlaces)]
        public override decimal? Price { get; set; }

        [PrecisionField(StoreDecimalPrecision)]
        [ScaleField(StoreDecimalPlaces)]
        public override decimal? PriceVAT { get; set; }

        [PrecisionField(StoreDecimalPrecision)]
        [ScaleField(StoreDecimalPlaces)]
        public override decimal? VAT { get; set; }
    }
}
